using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    // The single start-time reconciliation of the monitored container's DECLARED configuration against its GROUND TRUTH,
    // shared by both relay hosts. It closes three silent runtime failure modes that a startup throw makes loud:
    //   - a mistyped/reordered partition-key path recovers a null-component PartitionKey, so the delivered-stamp patch
    //     404s after the publish already succeeded and the same message re-publishes on every change-feed pass (#362);
    //   - a POSITIVE container defaultTtl deletes a still-pending outbox document (written with no ttl field) before the
    //     relay ever drains it, converting at-least-once into zero-times (#363);
    //   - a container partitioned on a path the relay itself patches ("/status" or "/ttl") can never be stamped, because
    //     the stamp could never land on the partition key, so every document publishes and stays pending and re-publishes.
    internal static class MonitoredContainerContract
    {
        internal static async Task VerifyAsync(Container monitoredContainer,
                                               IReadOnlyList<string> declaredPartitionKeyPath,
                                               CancellationToken cancellationToken)
        {
            _ = monitoredContainer ?? throw new ArgumentNullException(nameof(monitoredContainer));
            _ = declaredPartitionKeyPath ?? throw new ArgumentNullException(nameof(declaredPartitionKeyPath));

            // INVARIANT: the container properties are read EXACTLY ONCE per verification and ALL checks read that same
            // response, so adding a further ground-truth check never costs another metadata round-trip at start.
            ContainerProperties properties = await ReadPropertiesAsync(monitoredContainer, cancellationToken).ConfigureAwait(false);

            // EVERY check always runs and a single throw names every violation: an operator who fixed one, restarted, and
            // only then discovered another would pay a failed start per violation on one misconfigured container.
            var violations = new List<string>();
            AddWhenPresent(violations, DescribePartitionKeyViolation(declaredPartitionKeyPath, properties.PartitionKeyPaths));
            AddWhenPresent(violations, DescribeTimeToLiveViolation(properties.DefaultTimeToLive));
            AddWhenPresent(violations, DescribeStampedPathCollision(properties.PartitionKeyPaths));

            if (violations.Count == 0)
            {
                return;
            }

            throw new InvalidOperationException(BuildViolationMessage(monitoredContainer, violations));
        }

        private static async Task<ContainerProperties> ReadPropertiesAsync(Container monitoredContainer, CancellationToken cancellationToken)
        {
            try
            {
                ContainerResponse response = await monitoredContainer
                    .ReadContainerAsync(requestOptions: null, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return response.Resource;
            }
            catch (CosmosException cosmosException)
            {
                throw new InvalidOperationException(
                    $"The Cosmos outbox relay could not read the properties of monitored {DescribeContainer(monitoredContainer)}. The relay verifies that container's partition-key path and default time-to-live at start, so it requires metadata read access on the monitored container.",
                    cosmosException);
            }
        }

        private static void AddWhenPresent(ICollection<string> violations, string violation)
        {
            if (violation is not null)
            {
                violations.Add(violation);
            }
        }

        private static string DescribePartitionKeyViolation(IReadOnlyList<string> declaredPartitionKeyPath,
                                                            IReadOnlyList<string> actualPartitionKeyPaths)
        {
            if (PathsMatch(declaredPartitionKeyPath, actualPartitionKeyPaths))
            {
                return null;
            }

            return $"its declared partition-key path [{DescribePath(declaredPartitionKeyPath)}] does not match the container's actual partition-key path [{DescribePath(actualPartitionKeyPaths)}] (compared in order, segment for segment, case-sensitively)";
        }

        // An ALLOWLIST, not a reject-enumeration: only the two values that cannot purge a pending outbox document pass.
        // -1 is the intended mode (items carrying no ttl field never expire); unset passes with the documented tradeoff
        // that delivered documents are never purged. Every other value — positive, 0, or below -1 — is rejected.
        private static string DescribeTimeToLiveViolation(int? defaultTimeToLive)
        {
            if (defaultTimeToLive is null || defaultTimeToLive.Value == -1)
            {
                return null;
            }

            return $"its default time-to-live is {defaultTimeToLive.Value.ToString(CultureInfo.InvariantCulture)}, and a pending outbox document carries no ttl field, so Cosmos would delete it before the relay ever published it — the only accepted values are -1 (on, items without a ttl field never expire) and unset";
        }

        // The relay's delivered stamp patches BOTH the status path and "/ttl" on every drained document, and Cosmos
        // REJECTS a patch of the partition key. The paths are read off CosmosOutboxDocument.RelayStampedPaths — derived
        // from the very field constants the patch ops are built from — so this check can never drift from the ops it
        // guards. Comparison is the same discipline the partition-key match uses: canonicalized the way the relay itself
        // reads a path, ordinal, case-sensitive.
        private static string DescribeStampedPathCollision(IReadOnlyList<string> actualPartitionKeyPaths)
        {
            if (actualPartitionKeyPaths is null)
            {
                return null;
            }

            foreach (string actualPath in actualPartitionKeyPaths)
            {
                string collidingPath = FindStampedPath(actualPath);
                if (collidingPath is not null)
                {
                    return $"its actual partition-key path includes '{collidingPath}', which the relay patches on every drain — the stamp could never land on a document's partition-key path, so every published document would stay pending and publish again on the next pass, forever (this container's relay is already failing that way today; the check surfaces that defect rather than introducing it)";
                }
            }

            return null;
        }

        private static string FindStampedPath(string actualPath)
        {
            string canonicalPath = CanonicalizePath(actualPath);
            foreach (string stampedPath in CosmosOutboxDocument.RelayStampedPaths)
            {
                if (string.Equals(canonicalPath, stampedPath, StringComparison.Ordinal))
                {
                    return stampedPath;
                }
            }

            return null;
        }

        private static bool PathsMatch(IReadOnlyList<string> declaredPartitionKeyPath, IReadOnlyList<string> actualPartitionKeyPaths)
        {
            if (actualPartitionKeyPaths is null || actualPartitionKeyPaths.Count != declaredPartitionKeyPath.Count)
            {
                return false;
            }

            for (var i = 0; i < declaredPartitionKeyPath.Count; i++)
            {
                if (!string.Equals(CanonicalizePath(declaredPartitionKeyPath[i]), CanonicalizePath(actualPartitionKeyPaths[i]), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        // Both sides are canonicalized to the relay's OWN notion of a path before comparison, so the check accepts exactly
        // what the runtime already treats as the same path: every site that reads a partition-key path splits it on '/'
        // with RemoveEmptyEntries (CosmosOutboxRelay.NavigateToPathValue, CosmosPartitionKeyStamping.ExtractRootSegment
        // and StampPartitionKeySegment), so `tenantId`, `tenantId/` and `//tenantId` navigate, stamp and recover exactly
        // as `/tenantId` does. A path that yields NO segments (empty, or nothing but slashes) is returned UNCHANGED
        // rather than collapsed to a common empty form, so one such spelling can never compare equal to another and be
        // silently accepted; PartitionKeyPathValidator already rejects a whitespace declaration at registration, and the
        // stamping split throws on a path with no property name. Case is NOT folded — Cosmos paths are case-sensitive.
        private static string CanonicalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return path;
            }

            return "/" + string.Join("/", segments);
        }

        private static string DescribePath(IReadOnlyList<string> partitionKeyPath)
            => partitionKeyPath is null ? "<none>" : string.Join(", ", partitionKeyPath);

        private static string BuildViolationMessage(Container monitoredContainer, IReadOnlyList<string> violations)
            => $"The Cosmos outbox relay cannot monitor {DescribeContainer(monitoredContainer)}: {string.Join("; and ", violations)}. Correct the container or the relay registration and restart.";

        private static string DescribeContainer(Container monitoredContainer)
            => $"container '{monitoredContainer.Id}' in database '{monitoredContainer.Database?.Id}'";
    }
}
