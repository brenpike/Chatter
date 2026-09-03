using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Sending;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The testable core of the #222 document-tier change-feed outbox relay, isolated from the
    /// <see cref="ChangeFeedProcessor"/> plumbing that feeds it (that host lives in
    /// <see cref="CosmosOutboxRelayHostedService"/>). Given a single change-feed document — read as a
    /// <see cref="JsonElement"/> so Chatter owns the wire shape end-to-end (no Cosmos-SDK Newtonsoft serialization of
    /// the relay's reads) — it:
    /// <list type="number">
    /// <item>FILTERS to outbox documents only: the document's <c>_chatterType</c> discriminator must equal
    /// <see cref="CosmosItemId.OutboxKind"/> AND its <c>status</c> must equal <see cref="CosmosOutboxDocument.StatusPending"/>.
    /// A domain document, an inbox marker (<c>_chatterType="inbox"</c>), an already-delivered outbox document, or a
    /// malformed outbox document with a missing/empty status is NOT pending and is skipped — including the relay's OWN
    /// delivered/TTL update event (publish-once by construction).</item>
    /// <item>VERIFIES the document against <see cref="OutboxDocumentContract"/> and RECONSTRUCTS the
    /// <see cref="OutboundBrokeredMessage"/> from the descriptor that verification returns, mirroring
    /// <c>OutboxProcessor.Process</c> exactly (MessageId verbatim, Destination, MessageBody, MessageContentType,
    /// MessageContext materialized through <c>ChatterJson.Options</c> via <see cref="MessageContext.MaterializePersistedContext"/>,
    /// content-type fallback to the persisted MessageContext, infrastructure type read from the MessageContext). The
    /// contract is the SOLE classifier here — the reconstruction has no throwing classification path of its own, and
    /// the descriptor is its WHOLE input: after verification the reconstruct-and-publish path holds no
    /// <see cref="JsonElement"/>, so no document-derived value can reach it unverified.</item>
    /// <item>On a VIOLATION, stamps the document an Undeliverable Outbox Document instead of publishing it (#361): a
    /// single-op <see cref="Container.PatchItemAsync"/> setting <c>/status="undeliverable"</c> and deliberately NO
    /// <c>/ttl</c> op, then the undeliverable count, then the full violation text at <c>Error</c>. A document whose own
    /// persisted bytes prove it can never be reconstructed would otherwise wedge its lease forever: the drain throws,
    /// the batch is never checkpointed, and the identical document re-fails on every pass.</item>
    /// <item>PUBLISHES via <c>IMessagingInfrastructureProvider.GetDispatcher(infra).Dispatch(message, null)</c> — the
    /// SAME no-reliability-re-entry path the EF relational relay uses (NOT <c>IBrokeredMessageDispatcher</c>, which can
    /// route back through the outbox and recurse).</item>
    /// <item>On publish success, STAMPS delivered + TTL: a SINGLE <see cref="Container.PatchItemAsync"/> with two ops —
    /// set <c>/status="delivered"</c> and set <c>/ttl=&lt;positive seconds&gt;</c> — keyed by the document id and the
    /// partition key recovered from the change-feed document at the container's declared partition-key path. Cosmos then
    /// self-purges the delivered document once its TTL elapses, but ONLY when the container has <c>defaultTtl</c> enabled
    /// (<c>-1</c>). <c>MonitoredContainerContract</c> verifies at start that the container's <c>defaultTtl</c> cannot PURGE
    /// a still-pending document; it does NOT require purge to be on, because it also accepts <c>defaultTtl</c> UNSET — and
    /// under unset this stamp is written but Cosmos expires nothing, so delivered documents are never purged.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// AT-LEAST-ONCE. Publish and the delivered/TTL patch are two separate writes: a publish that succeeds and then a
    /// patch that fails leaves the document <c>pending</c>, so it re-surfaces on the next change-feed pass and is
    /// re-published — downstream consumers dedup via the #220 document-tier inbox marker. A publish that THROWS performs
    /// NO patch and PROPAGATES the exception out of <see cref="ProcessChangeAsync"/>; the host lets it escape the
    /// change-feed handler so the SDK does NOT checkpoint the batch (the document re-surfaces next pass) rather than
    /// advancing the lease past an unpublished document.
    /// A delivered stamp that fails AFTER a publish is raised as <see cref="OutboxConfirmationFailedException"/> (#416)
    /// so the fault survives the unwind to the host, which is the one component that knows the Lease Token it happened
    /// under. The phase is derived from this relay's OWN control flow — whether the dispatch returned — never from what
    /// the stamp threw; the drop path (a resolver that published nothing) raises no carrier, because with no publish
    /// there is no republication to brake on. The relay itself consults NO gate: it reports the Confirmation Failure
    /// and the host decides.
    /// </remarks>
    internal sealed class CosmosOutboxRelay
    {
        /// <summary>A drain publishes exactly one document, so the batch count on its send span is always one.</summary>
        private const int DrainedMessageCount = 1;

        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly OutboxDeliverySettings _settings;
        private readonly GuardedRelayLog _log;

        // The pre-seam constructor. Maps to the no-resolver verbatim-reconstruction path with the original hard-coded
        // drain behavior (OutboxDeliverySettings.Legacy) so CosmosOutboxRelayHostedService — which constructs the relay
        // this way — stays byte-identical.
        public CosmosOutboxRelay(IMessagingInfrastructureProvider infrastructureProvider, IBodyConverterFactory bodyConverterFactory, GuardedRelayLog log = default)
            : this(infrastructureProvider, bodyConverterFactory, OutboxDeliverySettings.Legacy, log)
        {
        }

        // The seam constructor. OutboxDeliverySettings carries the admission gate (the always-applied id-guard plus any
        // narrowing filter) and the delivered/TTL stamp knobs. The IOutboxBodyResolver is NOT held here — it is supplied
        // per-call to ProcessChangeAsync so the relay never carries a resolver it might silently drop.
        // The log is OPTIONAL and defaults to an omitted sink. GuardedRelayLog is a readonly struct holding a nullable
        // ILogger, so an omitted logger is a silent no-op BY CONSTRUCTION rather than by a null check at each use, and
        // the relay holds THIS instead of a raw ILogger so an unguarded optional-sink log call is unreachable from it.
        internal CosmosOutboxRelay(IMessagingInfrastructureProvider infrastructureProvider,
                                   IBodyConverterFactory bodyConverterFactory,
                                   OutboxDeliverySettings settings,
                                   GuardedRelayLog log = default)
        {
            _infrastructureProvider = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log;
        }

        /// <summary>
        /// Processes a single change-feed document against <paramref name="monitoredContainer"/> (the container the
        /// document lives in, used for the delivered/TTL patch). <paramref name="partitionKeyPath"/> is the container's
        /// declared partition-key path (single or hierarchical) — the document carries its partition-key value(s) at
        /// these segments, and the delivered/TTL patch must target the SAME logical partition. A non-admitted document is
        /// a no-op. An admitted document is verified against <see cref="OutboxDocumentContract"/>, reconstructed from
        /// that verification, published, then patched delivered+TTL; one the contract rejects is stamped undeliverable
        /// instead and never published. A publish failure performs no patch and propagates so the host does not
        /// checkpoint the change-feed batch.
        /// </summary>
        public Task ProcessChangeAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken = default)
            => ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, resolver: null, cancellationToken);

        /// <summary>
        /// Processes a single change-feed document, with an optional per-call <paramref name="resolver"/> owning the
        /// brokered message to publish for an admitted document. When <paramref name="resolver"/> is null the verbatim
        /// reconstruction path is used. A non-admitted document is a no-op. A publish (or resolver) failure performs no
        /// patch and propagates so the host does not checkpoint the change-feed batch.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the Outbox Document Contract is evaluated ONLY on the no-resolver verbatim path. A supplied
        /// resolver OWNS the message, so the document's persisted fields need not be publishable at all — verifying
        /// them here would give up on documents the resolver publishes perfectly well (the thin-trigger shape carries
        /// no body, content type or context by design).
        /// </remarks>
        internal async Task ProcessChangeAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, IOutboxBodyResolver resolver, CancellationToken cancellationToken = default)
        {
            _ = monitoredContainer ?? throw new ArgumentNullException(nameof(monitoredContainer));
            _ = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));

            if (!_settings.IsAdmitted(document))
            {
                // INVARIANT: ADR-0010 R1 - the outcome value is resolved INSIDE this module's own off-guard. Passing
                // it as an argument to a guarded emit method would build it unconditionally, because C# evaluates
                // arguments before the callee's guard runs.
                if (CosmosReliabilityDiagnostics.IsEnabled)
                {
                    CosmosReliabilityDiagnostics.RecordDrainedDocument(CosmosReliabilityDiagnostics.DrainOutcomes.Skipped);
                }

                return;
            }

            // The lag is recorded ONCE, at ADMISSION, so a document whose reconstruction, resolution or publish then
            // THROWS still reports how long it had been pending — the very case the measurement exists to expose.
            if (CosmosReliabilityDiagnostics.IsEnabled)
            {
                RecordAdmissionLag(document);
            }

            bool messageDispatched;
            if (resolver is null)
            {
                // No resolver supplied: the verbatim reconstruction path runs through the Outbox Document Contract,
                // which is the SOLE classifier of whether the persisted fields can become a brokered message.
                OutboxDocumentVerification verification = OutboxDocumentContract.Verify(document);
                if (!verification.IsSatisfied)
                {
                    // An undeliverable stamp happens INSTEAD of a publish, never after one, and records NO drain
                    // outcome: the document never resolved to a publish decision, the same reason a failed publish
                    // records none. The admitted/skipped/dropped vocabulary is CLOSED.
                    await MarkUndeliverableAsync(document, verification, monitoredContainer, partitionKeyPath, cancellationToken);
                    return;
                }

                await DispatchAsync(Reconstruct(verification), verification.MessagingSystem);
                messageDispatched = true;
            }
            else
            {
                // A resolver is supplied: it owns the message to publish. A NON-NULL resolution is dispatched; a NULL
                // resolution dispatches nothing. Either way the document is then stamped delivered (a null resolution is
                // an intentional drop-and-acknowledge). A THROW propagates below with no stamp issued.
                OutboxDrainContext context = BuildDrainContext(document, partitionKeyPath);
                OutboundBrokeredMessage resolved = await resolver.ResolveAsync(context, cancellationToken);
                messageDispatched = resolved is not null;
                if (messageDispatched)
                {
                    // The resolved message's context is HOST-owned and is never verified, so its messaging system comes
                    // from the module's SINGLE reader, without classification.
                    await DispatchAsync(resolved, OutboxDocumentContract.ReadMessagingSystem(resolved.MessageContext));
                }
            }

            if (CosmosReliabilityDiagnostics.IsEnabled)
            {
                CosmosReliabilityDiagnostics.RecordDrainedDocument(messageDispatched
                    ? CosmosReliabilityDiagnostics.DrainOutcomes.Admitted
                    : CosmosReliabilityDiagnostics.DrainOutcomes.Dropped);
            }

            await StampDeliveredAsync(document, monitoredContainer, partitionKeyPath, messageDispatched, cancellationToken);
        }

        // PUBLISH via IMessagingInfrastructureProvider.GetDispatcher(infra).Dispatch(message, null) — the SAME
        // no-reliability-re-entry path the EF relational relay uses (NOT IBrokeredMessageDispatcher, which can route
        // back through the outbox and recurse). A throw propagates to the caller with no delivered/TTL patch issued,
        // leaving the document pending (at-least-once).
        // INVARIANT: the messaging system arrives as a PARAMETER, already read as a string by
        // OutboxDocumentContract.ReadMessagingSystem — on the verbatim path via the verified descriptor, on the resolver
        // path from the resolved message's own context. Nothing is read off a message context here, so no cast of a
        // context value can fault this publish.
        private async Task DispatchAsync(OutboundBrokeredMessage message, string messagingSystem)
        {
            IMessagingInfrastructureDispatcher dispatcher = _infrastructureProvider.GetDispatcher(messagingSystem);

            // INVARIANT: ADR-0010 R1/R4 - Chatter's own off-guard is what decides, and it decides HERE rather than
            // inside the scope. Argument evaluation precedes the guard INSIDE SendScope.Open, so a call site that
            // reaches DispatchObserved has already resolved the persisted parent and has already entered a second
            // async state machine. An application that never opted into broker diagnostics therefore takes the same
            // bare dispatch it took before this hop was instrumented, matching the relational drain and the three
            // sibling send sites.
            if (!BrokerDiagnostics.IsEnabled)
            {
                await dispatcher.Dispatch(message, null);
            }
            else
            {
                await DispatchObserved(dispatcher, message, messagingSystem);
            }
        }

        /// <summary>
        /// Publishes ONE drained Outbox Document to broker infrastructure under its own send span (ADR-0010 D7), so
        /// the hop that actually reaches the broker — long after the write, in another process, where it can fail
        /// entirely on its own — is observable rather than silent.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the dispatch stays BELOW the Router. The drain calls the messaging-infrastructure dispatcher
        /// directly, deliberately, so replaying a document cannot re-enter the reliability pipeline and write it to
        /// the outbox again; the scope only OBSERVES the call that was already being made and reroutes nothing.
        /// The Messaging Infrastructure the persisted context names is the only messaging-system identity this drain
        /// has; it is passed through AS-IS, and <see cref="BrokerDiagnostics"/> normalizes a blank identifier to an
        /// unset span attribute rather than inventing one.
        /// </remarks>
        private static async Task DispatchObserved(IMessagingInfrastructureDispatcher dispatcher, OutboundBrokeredMessage message, string messagingSystem)
        {
            using (var scope = SendScope.Open(messagingSystem, BrokerDiagnostics.OperationTypes.Send, message.Destination, DrainedMessageCount, ResolvePersistedParent(message.MessageContext)))
            {
                // OVERWRITES the persisted write-time traceparent with this hop's, because the drain IS the send that
                // put the message on the broker and is therefore what a downstream receive must parent to. The trace
                // stays intact: the span whose context is written here is itself a child of the context it replaced.
                // The overwrite happens ONLY when the scope has a trace context to travel - with diagnostics off and
                // on a sampled-out DEFERRED send, Inject writes nothing and the persisted record rides out unchanged
                // (ADR-0010 R2).
                scope.Inject(message.MessageContext);

                try
                {
                    await dispatcher.Dispatch(message, null);
                }
                catch (Exception e)
                {
                    scope.RecordFailure(e);
                    throw;
                }
            }
        }

        /// <summary>
        /// Reads back the trace context the WRITER persisted with the Outbox Document, or <c>default</c> when the
        /// document carries none — one written while diagnostics were off, or received over a path that propagates
        /// no context.
        /// </summary>
        /// <remarks>
        /// INVARIANT: ADR-0010 R1 — the drain call site has ALREADY run Chatter's own off-guard, so an application
        /// that never opted in never reaches this method. The guard is repeated as the FIRST statement here so the
        /// helper stays safe to call from a site that has not, and so no extraction can precede it either way.
        /// INVARIANT: <c>default</c> means ABSENCE, never "use the current activity". The deferred
        /// <see cref="SendScope"/> overload starts a FRESH ROOT for it rather than adopting the change feed's ambient
        /// activity, which would report that the feed caused the message when the write did (ADR-0010 D6).
        /// </remarks>
        private static ActivityContext ResolvePersistedParent(IDictionary<string, object> messageContext)
        {
            if (!BrokerDiagnostics.IsEnabled)
            {
                return default;
            }

            TraceContextPropagator.TryExtractFromMessageContext(messageContext, out var persistedParent);
            return persistedParent;
        }

        // Reports how long the admitted document had been pending, from the RAW Cosmos _ts the document carries.
        // INVARIANT: the raw Unix-epoch-seconds value is handed over verbatim — the age and its clock-skew clamp are
        // derived by CosmosReliabilityDiagnostics, so this call site can never record a lag it computed itself. A
        // document with no _ts (one that never went through Cosmos) records no lag; its outcome is still counted.
        private static void RecordAdmissionLag(JsonElement document)
        {
            if (!CosmosOutboxDocument.TryGetInt64(document, CosmosOutboxDocument.TimestampField, out long enqueuedUnixSeconds))
            {
                return;
            }

            CosmosReliabilityDiagnostics.RecordDrainLag(enqueuedUnixSeconds);
        }

        // Builds the per-document context handed to an IOutboxBodyResolver: the verbatim MessageId (read via the shared
        // CosmosOutboxDocument.TryGetString), the partition key recovered via the SAME recovery the delivered/TTL stamp
        // uses, the container's declared partition-key path, and the raw document.
        private static OutboxDrainContext BuildDrainContext(JsonElement document, IReadOnlyList<string> partitionKeyPath)
        {
            CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.MessageIdField, out string messageId);
            PartitionKey partitionKey = RecoverPartitionKey(document, partitionKeyPath);
            return new OutboxDrainContext(messageId, partitionKey, partitionKeyPath, document);
        }

        // RECONSTRUCT the OutboundBrokeredMessage from the VERIFIED descriptor the Outbox Document Contract produced:
        // the verbatim message id, the resolved content type, the destination, the persisted body string and the
        // materialized message context.
        // INVARIANT: the descriptor is the ONLY input — this method takes NO JsonElement, deliberately. Every value the
        // reconstruction and the publish consume has passed the contract, which reads each context value AS its type
        // rather than casting to it; re-reading one off the document here would reintroduce exactly the
        // InvalidCastException a non-string persisted value used to fault the whole drain with. There is no further
        // field to find because there is no document in scope to read one from.
        private OutboundBrokeredMessage Reconstruct(OutboxDocumentVerification verification)
        {
            IBrokeredMessageBodyConverter converter = _bodyConverterFactory.CreateBodyConverter(verification.ContentType);

            return new OutboundBrokeredMessage(verification.MessageId, converter.GetBytes(verification.MessageBody), verification.MessageContext, verification.Destination, converter);
        }

        /// <summary>
        /// Gives up on one Outbox Document the contract proved unpublishable: STAMP, then EMIT, then LOG.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the ORDER is load-bearing. A give-up cannot be recorded in a container that is not accepting
        /// writes, so a failing stamp must propagate BEFORE anything reports a give-up that did not happen. The batch
        /// is then not checkpointed, the identical IMMUTABLE document re-surfaces, and the next pass re-evaluates it —
        /// through a total, pure verification — to the identical verdict. A give-up blocked by an unavailable container
        /// therefore SELF-HEALS rather than being lost.
        /// </remarks>
        private async Task MarkUndeliverableAsync(JsonElement document,
                                                  OutboxDocumentVerification verification,
                                                  Container monitoredContainer,
                                                  IReadOnlyList<string> partitionKeyPath,
                                                  CancellationToken cancellationToken)
        {
            await StampUndeliverableAsync(document, monitoredContainer, partitionKeyPath, cancellationToken);

            // INVARIANT: no outer ADR-0010 R1 off-guard is needed here, and adding one would be noise: this emit takes
            // no argument, so nothing is BUILT before the emit method's own instrument guard runs.
            CosmosReliabilityDiagnostics.RecordUndeliverableDocument();

            // ALWAYS-ON, at Error, through the guarded sink. The undeliverable count deliberately carries no violation
            // attribute — one document can violate several contract facts at once — so this is the only channel
            // carrying WHICH facts it violated, and the only channel of any kind for a meter-less application. It
            // carries no exception because nothing threw: the verdict came from the document's own bytes.
            _log.Error(exception: null,
                       "The Cosmos Outbox Relay marked an Outbox Document undeliverable and stopped republishing it. {Violation}",
                       verification.ViolationMessage);
        }

        // THE GIVE-UP STAMP: a SINGLE-OP patch setting the status path to the fixed terminal undeliverable status. It
        // deliberately carries NO ttl op — an Undeliverable Outbox Document is the evidence of the defect that produced
        // it, so it is never scheduled for self-purge and never deleted.
        private async Task StampUndeliverableAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken)
        {
            var patchOperations = new List<PatchOperation>
            {
                PatchOperation.Set(_settings.StatusPatchPath, CosmosOutboxDocument.StatusUndeliverable),
            };

            await PatchAsync(document, monitoredContainer, partitionKeyPath, patchOperations, cancellationToken);
        }

        // POST-PUBLISH: a SINGLE PatchItemAsync with two ops (set the status path to the delivered value, set the
        // Cosmos-reserved "/ttl" to a positive seconds value), keyed by the document id read off the change-feed item and
        // the partition key recovered from the same item at the container's declared partition-key path. The status path,
        // the delivered value, and the ttl seconds come from the configured OutboxDeliverySettings; the ttl PATH is
        // hard-wired to "/" + CosmosOutboxDocument.TtlField (the only field Cosmos self-purges on), so a non-purging
        // delivered stamp is unrepresentable. Legacy reproduces the original /status="delivered" + /ttl=86400 stamp
        // byte-for-byte. PatchItem (not ReplaceItem) so only the two delivery fields are touched and the aggregate-shaped
        // wire body is left untouched.
        // A stamp that fails AFTER a publish is a Confirmation Failure (#416): the message is on the broker, the batch
        // is not checkpointed, and the same document republishes on every pass. It is raised as
        // OutboxConfirmationFailedException so it survives the unwind to the host, which is the one component that
        // knows the Lease Token it happened under.
        // INVARIANT: the phase comes from this method's OWN control flow — the messageDispatched the caller derived
        // from whether the dispatch returned — never from what the stamp threw. The dropped path (nothing published)
        // takes the exception filter's false arm, so its fault propagates untouched: no publish, no amplification, and
        // nothing for a host to brake on.
        private async Task StampDeliveredAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, bool messageDispatched, CancellationToken cancellationToken)
        {
            var patchOperations = new List<PatchOperation>
            {
                PatchOperation.Set(_settings.StatusPatchPath, _settings.DeliveredStatusValue),
                PatchOperation.Set("/" + CosmosOutboxDocument.TtlField, _settings.DeliveredTtlSeconds),
            };

            try
            {
                await PatchAsync(document, monitoredContainer, partitionKeyPath, patchOperations, cancellationToken);
            }
            catch (Exception confirmationFailure) when (messageDispatched)
            {
                throw new OutboxConfirmationFailedException(confirmationFailure);
            }
        }

        // The one write both stamps go through: keyed by the document id read off the change-feed item and the
        // partition key recovered from the same item at the container's declared partition-key path, so a stamp always
        // lands on the document it was decided for.
        private static async Task PatchAsync(JsonElement document,
                                             Container monitoredContainer,
                                             IReadOnlyList<string> partitionKeyPath,
                                             IReadOnlyList<PatchOperation> patchOperations,
                                             CancellationToken cancellationToken)
        {
            if (!CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.IdField, out string id) || string.IsNullOrEmpty(id))
            {
                throw new InvalidOperationException("A pending outbox document is missing its 'id'; cannot stamp it.");
            }

            PartitionKey partitionKey = RecoverPartitionKey(document, partitionKeyPath);

            await monitoredContainer.PatchItemAsync<JsonElement>(id, partitionKey, patchOperations, requestOptions: null, cancellationToken: cancellationToken);
        }

        // Recovers the document's partition key by reading the value(s) the document carries at the container's declared
        // partition-key path (single or hierarchical) and building a PartitionKey that preserves each value's JSON kind
        // (string/number/bool/null), so the delivered/TTL patch lands in the SAME logical partition the document lives
        // in. A path segment may be nested (e.g. "/tenant/id"), navigated object-by-object; a missing/array-valued
        // intermediate yields a JSON-null component, mirroring how the document was stamped on write.
        private static PartitionKey RecoverPartitionKey(JsonElement document, IReadOnlyList<string> partitionKeyPath)
        {
            var builder = new PartitionKeyBuilder();
            foreach (string path in partitionKeyPath)
            {
                JsonElement component = NavigateToPathValue(document, path);
                switch (component.ValueKind)
                {
                    case JsonValueKind.String:
                        builder.Add(component.GetString());
                        break;
                    case JsonValueKind.Number:
                        builder.Add(component.GetDouble());
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        builder.Add(component.GetBoolean());
                        break;
                    default:
                        builder.AddNullValue();
                        break;
                }
            }

            return builder.Build();
        }

        // Navigates the document to the value at a partition-key path segment (e.g. "/tenant/id"), descending one nested
        // object per path part. Returns an undefined JsonElement (ValueKind == Undefined) when any part is absent or a
        // non-object intermediate is hit, which RecoverPartitionKey maps to a JSON-null partition-key component.
        private static JsonElement NavigateToPathValue(JsonElement document, string path)
        {
            JsonElement current = document;
            foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out JsonElement next))
                {
                    return default;
                }

                current = next;
            }

            return current;
        }
    }
}
