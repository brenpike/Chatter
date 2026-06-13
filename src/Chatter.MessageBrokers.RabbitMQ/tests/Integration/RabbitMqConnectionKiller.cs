using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Forces RabbitMQ.Client automatic recovery by killing the broker-side AMQP connection(s) out-of-band via the
    // RabbitMQ management HTTP API (DELETE /api/connections/{name}). The receive channel rides an
    // AutomaticRecoveryEnabled connection, so when the broker drops the connection the client transparently
    // recovers the SAME IChannel — the exact path that leaves the receive-channel epoch stale unless the source's
    // RecoverySucceededAsync subscription advances it. Used only by the recovery-epoch integration proof.
    //
    // The management port (15672) is derived from the AMQP URI's host and credentials; the management plugin is
    // present because the fixture pins the `-management` image. Connections are listed and deleted by name.
    internal static class RabbitMqConnectionKiller
    {
        private const int ManagementPort = 15672;
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

        // Kills every current broker connection except those whose client-provided name marks them as a management
        // or out-of-band helper. Returns the number of connections deleted so the caller can assert at least one
        // (the receiver's) was dropped, forcing recovery.
        public static async Task<int> KillAllConnectionsAsync(string amqpUri, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(amqpUri))
            {
                throw new ArgumentException("An AMQP connection URI is required.", nameof(amqpUri));
            }

            var uri = new Uri(amqpUri);
            var userInfo = uri.UserInfo.Split(':', 2);
            var userName = userInfo.Length > 0 && userInfo[0].Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "guest";
            var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "guest";

            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationCts.CancelAfter(OperationTimeout);
            var token = operationCts.Token;

            var managementBase = new UriBuilder("http", uri.Host, ManagementPort).Uri;

            using var client = new HttpClient { BaseAddress = managementBase, Timeout = OperationTimeout };
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            var names = await ListConnectionNamesAsync(client, token).ConfigureAwait(false);

            var deleted = 0;
            foreach (var name in names)
            {
                var encoded = Uri.EscapeDataString(name);
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/connections/{encoded}");
                // Ask the broker to close with a non-empty reason so the client sees a clean shutdown and recovers.
                request.Headers.Add("X-Reason", "recovery-epoch-integration-test forced close");
                using var response = await client.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    deleted++;
                }
            }

            return deleted;
        }

        private static async Task<IReadOnlyList<string>> ListConnectionNamesAsync(HttpClient client, CancellationToken token)
        {
            using var response = await client.GetAsync("/api/connections", token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

            var names = new List<string>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        names.Add(name.GetString());
                    }
                }
            }

            return names;
        }
    }
}
