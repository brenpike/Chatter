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
    /// <item>RECONSTRUCTS the <see cref="OutboundBrokeredMessage"/> from the persisted fields, mirroring
    /// <c>OutboxProcessor.Process</c> exactly (MessageId verbatim, Destination, MessageBody, MessageContentType,
    /// MessageContext materialized through <c>ChatterJson.Options</c> via <see cref="MessageContext.MaterializePersistedContext"/>,
    /// content-type fallback to the persisted MessageContext, infrastructure type read from the MessageContext).</item>
    /// <item>PUBLISHES via <c>IMessagingInfrastructureProvider.GetDispatcher(infra).Dispatch(message, null)</c> — the
    /// SAME no-reliability-re-entry path the EF relational relay uses (NOT <c>IBrokeredMessageDispatcher</c>, which can
    /// route back through the outbox and recurse).</item>
    /// <item>On publish success, STAMPS delivered + TTL: a SINGLE <see cref="Container.PatchItemAsync"/> with two ops —
    /// set <c>/status="delivered"</c> and set <c>/ttl=&lt;positive seconds&gt;</c> — keyed by the document id and the
    /// partition key recovered from the change-feed document at the container's declared partition-key path. Cosmos then
    /// self-purges the delivered document once its TTL elapses (the container MUST have <c>defaultTtl</c> enabled — a
    /// startup prerequisite the host verifies through the <c>MonitoredContainerContract</c>, not a silent assumption).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// AT-LEAST-ONCE. Publish and the delivered/TTL patch are two separate writes: a publish that succeeds and then a
    /// patch that fails leaves the document <c>pending</c>, so it re-surfaces on the next change-feed pass and is
    /// re-published — downstream consumers dedup via the #220 document-tier inbox marker. A publish that THROWS performs
    /// NO patch and PROPAGATES the exception out of <see cref="ProcessChangeAsync"/>; the host lets it escape the
    /// change-feed handler so the SDK does NOT checkpoint the batch (the document re-surfaces next pass) rather than
    /// advancing the lease past an unpublished document.
    /// </remarks>
    internal sealed class CosmosOutboxRelay
    {
        /// <summary>A drain publishes exactly one document, so the batch count on its send span is always one.</summary>
        private const int DrainedMessageCount = 1;

        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly OutboxDeliverySettings _settings;

        // The pre-seam constructor. Maps to the no-resolver verbatim-reconstruction path with the original hard-coded
        // drain behavior (OutboxDeliverySettings.Legacy) so CosmosOutboxRelayHostedService — which constructs the relay
        // this way — stays byte-identical.
        public CosmosOutboxRelay(IMessagingInfrastructureProvider infrastructureProvider, IBodyConverterFactory bodyConverterFactory)
            : this(infrastructureProvider, bodyConverterFactory, OutboxDeliverySettings.Legacy)
        {
        }

        // The seam constructor. OutboxDeliverySettings carries the admission gate (the always-applied id-guard plus any
        // narrowing filter) and the delivered/TTL stamp knobs. The IOutboxBodyResolver is NOT held here — it is supplied
        // per-call to ProcessChangeAsync so the relay never carries a resolver it might silently drop.
        internal CosmosOutboxRelay(IMessagingInfrastructureProvider infrastructureProvider,
                                   IBodyConverterFactory bodyConverterFactory,
                                   OutboxDeliverySettings settings)
        {
            _infrastructureProvider = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Processes a single change-feed document against <paramref name="monitoredContainer"/> (the container the
        /// document lives in, used for the delivered/TTL patch). <paramref name="partitionKeyPath"/> is the container's
        /// declared partition-key path (single or hierarchical) — the document carries its partition-key value(s) at
        /// these segments, and the delivered/TTL patch must target the SAME logical partition. A non-admitted document is
        /// a no-op. An admitted document is reconstructed verbatim, published, then patched delivered+TTL. A publish
        /// failure performs no patch and propagates so the host does not checkpoint the change-feed batch.
        /// </summary>
        public Task ProcessChangeAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken = default)
            => ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, resolver: null, cancellationToken);

        /// <summary>
        /// Processes a single change-feed document, with an optional per-call <paramref name="resolver"/> owning the
        /// brokered message to publish for an admitted document. When <paramref name="resolver"/> is null the verbatim
        /// reconstruction path is used. A non-admitted document is a no-op. A publish (or resolver) failure performs no
        /// patch and propagates so the host does not checkpoint the change-feed batch.
        /// </summary>
        internal Task ProcessChangeAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, IOutboxBodyResolver resolver, CancellationToken cancellationToken = default)
            => ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, resolver, new OutboxDrainAttempt(), cancellationToken);

        /// <summary>
        /// Processes a single change-feed document, reporting into <paramref name="attempt"/> the PHASE the drain
        /// reached: <see cref="OutboxDrainAttempt.MarkPublished"/> is called the instant the publish RETURNS, and
        /// nowhere else. A caller holding the attempt can therefore tell a PRE-publish failure (nothing went out) from
        /// a POST-publish one (the message is already on the broker) without classifying the exception.
        /// </summary>
        /// <remarks>
        /// The attempt is supplied by the CALLER, not owned here, precisely so it outlives a drain that THROWS. The
        /// overloads above pass a throwaway attempt, so a caller that does not care about the phase is unaffected.
        /// </remarks>
        internal async Task ProcessChangeAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, IOutboxBodyResolver resolver, OutboxDrainAttempt attempt, CancellationToken cancellationToken = default)
        {
            _ = monitoredContainer ?? throw new ArgumentNullException(nameof(monitoredContainer));
            _ = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));
            _ = attempt ?? throw new ArgumentNullException(nameof(attempt));

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
                // No resolver supplied: the verbatim reconstruction path is unchanged — reconstruct the brokered message
                // from the persisted outbox fields and publish it.
                await DispatchAsync(Reconstruct(document));
                attempt.MarkPublished();
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
                    await DispatchAsync(resolved);
                    attempt.MarkPublished();
                }
            }

            if (CosmosReliabilityDiagnostics.IsEnabled)
            {
                CosmosReliabilityDiagnostics.RecordDrainedDocument(messageDispatched
                    ? CosmosReliabilityDiagnostics.DrainOutcomes.Admitted
                    : CosmosReliabilityDiagnostics.DrainOutcomes.Dropped);
            }

            await StampDeliveredAsync(document, monitoredContainer, partitionKeyPath, cancellationToken);
        }

        // PUBLISH via IMessagingInfrastructureProvider.GetDispatcher(infra).Dispatch(message, null) — the SAME
        // no-reliability-re-entry path the EF relational relay uses (NOT IBrokeredMessageDispatcher, which can route
        // back through the outbox and recurse). The infrastructure type is read from the message's MessageContext. A
        // throw propagates to the caller with no delivered/TTL patch issued, leaving the document pending (at-least-once).
        private async Task DispatchAsync(OutboundBrokeredMessage message)
        {
            IDictionary<string, object> messageContext = message.MessageContext;
            messageContext.TryGetValue(MessageContext.InfrastructureType, out var infra);
            var messagingSystem = (string)infra;
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

        // RECONSTRUCT the OutboundBrokeredMessage from the persisted outbox fields, mirroring OutboxProcessor.Process:
        // MessageId verbatim, Destination, MessageBody, MessageContentType, MessageContext materialized through
        // ChatterJson.Options; content-type falls back to the persisted MessageContext when the doc content-type is
        // empty; the body bytes come from an IBodyConverterFactory converter for the resolved content type.
        private OutboundBrokeredMessage Reconstruct(JsonElement document)
        {
            CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.MessageIdField, out string messageId);
            CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.DestinationField, out string destination);
            CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.MessageBodyField, out string messageBody);
            CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.MessageContentTypeField, out string messageContentType);
            CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.MessageContextField, out string serializedMessageContext);

            // MaterializePersistedContext deserializes the persisted MessageContext JSON string through
            // ChatterJson.Options, restoring the CLR types the typed (string)/(DateTime?)/integer reads downstream
            // depend on (parity with OutboxProcessor.Process).
            IDictionary<string, object> messageContext = MessageContext.MaterializePersistedContext(serializedMessageContext);

            string contentType = messageContentType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = messageContext.TryGetValue(MessageContext.ContentType, out var ct) ? (string)ct : null;
            }

            if (string.IsNullOrWhiteSpace(contentType))
            {
                throw new InvalidOperationException(
                    $"Outbox document '{messageId}' has no content type in the document or its message context; a content type is required to serialize and publish the brokered message.");
            }

            IBrokeredMessageBodyConverter converter = _bodyConverterFactory.CreateBodyConverter(contentType);

            return new OutboundBrokeredMessage(messageId, converter.GetBytes(messageBody), messageContext, destination, converter);
        }

        // POST-PUBLISH: a SINGLE PatchItemAsync with two ops (set the status path to the delivered value, set the
        // Cosmos-reserved "/ttl" to a positive seconds value), keyed by the document id read off the change-feed item and
        // the partition key recovered from the same item at the container's declared partition-key path. The status path,
        // the delivered value, and the ttl seconds come from the configured OutboxDeliverySettings; the ttl PATH is
        // hard-wired to "/" + CosmosOutboxDocument.TtlField (the only field Cosmos self-purges on), so a non-purging
        // delivered stamp is unrepresentable. Legacy reproduces the original /status="delivered" + /ttl=86400 stamp
        // byte-for-byte. PatchItem (not ReplaceItem) so only the two delivery fields are touched and the aggregate-shaped
        // wire body is left untouched.
        private Task StampDeliveredAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken)
        {
            var patchOperations = new List<PatchOperation>
            {
                PatchOperation.Set(_settings.StatusPatchPath, _settings.DeliveredStatusValue),
                PatchOperation.Set("/" + CosmosOutboxDocument.TtlField, _settings.DeliveredTtlSeconds),
            };

            return PatchStampAsync(document, monitoredContainer, partitionKeyPath, "delivered", patchOperations, cancellationToken);
        }

        /// <summary>
        /// GIVES UP on a document the configured <see cref="OutboxPoisonPolicy"/> has seen fail consecutively often enough
        /// (#361): a SINGLE <see cref="Container.PatchItemAsync"/> with exactly ONE op — set the status path to the poison
        /// value — so <see cref="CosmosOutboxDocument.IsPendingOutbox"/> stops admitting it and the change feed can advance
        /// past it instead of re-throwing on it forever.
        /// </summary>
        /// <remarks>
        /// The document is NEVER deleted and carries NO ttl op: unlike a delivered document, a given-up one is the evidence
        /// of the defect that stalled the relay, so it must stay in the container and stay inspectable indefinitely.
        /// INVARIANT: the id and the partition key are resolved by the SAME reads the delivered stamp uses, so a
        /// misconfigured partition-key path makes the poison patch fail exactly as the delivered patch would and PROPAGATES.
        /// A configuration error must not be laundered into "give up on everything".
        /// </remarks>
        internal Task StampPoisonedAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken = default)
        {
            _ = monitoredContainer ?? throw new ArgumentNullException(nameof(monitoredContainer));
            _ = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));

            var patchOperations = new List<PatchOperation>
            {
                PatchOperation.Set(_settings.StatusPatchPath, _settings.PoisonPolicy.PoisonStatusValue),
            };

            return PatchStampAsync(document, monitoredContainer, partitionKeyPath, "poisoned", patchOperations, cancellationToken);
        }

        /// <summary>
        /// Bounds a POST-PUBLISH failure: a SINGLE <see cref="Container.PatchItemAsync"/> with exactly ONE op — set the
        /// status path to the configured unconfirmed value — for a document whose brokered message ALREADY reached the
        /// broker but whose delivered stamp could not be confirmed. <see cref="CosmosOutboxDocument.IsPendingOutbox"/>
        /// stops admitting it, so the change feed advances past it instead of re-publishing it forever.
        /// </summary>
        /// <remarks>
        /// The document is NEVER deleted and carries NO ttl op: like a given-up document, a published-unconfirmed one is
        /// the evidence of a delivery nobody could confirm, so it must stay in the container and stay inspectable
        /// indefinitely.
        /// INVARIANT: the id and the partition key are resolved by the SAME reads the delivered and poison stamps use, so
        /// a misconfigured partition-key path makes this patch fail exactly as those would and PROPAGATES. A
        /// configuration error must not be laundered into "give up on everything".
        /// </remarks>
        internal Task StampUnconfirmedAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken = default)
        {
            _ = monitoredContainer ?? throw new ArgumentNullException(nameof(monitoredContainer));
            _ = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));

            var patchOperations = new List<PatchOperation>
            {
                PatchOperation.Set(_settings.StatusPatchPath, _settings.UnconfirmedStatusValue),
            };

            return PatchStampAsync(document, monitoredContainer, partitionKeyPath, "unconfirmed", patchOperations, cancellationToken);
        }

        // Issues ONE PatchItemAsync for a status stamp, keyed by the document id read off the change-feed item and the
        // partition key recovered from the SAME item at the container's declared partition-key path. Shared by the
        // delivered stamp and the #361 poison stamp so both key on identical ground truth by construction; only the patch
        // operations differ. <paramref name="stampKind"/> names the stamp in the missing-id failure.
        private static Task PatchStampAsync(JsonElement document,
                                            Container monitoredContainer,
                                            IReadOnlyList<string> partitionKeyPath,
                                            string stampKind,
                                            IReadOnlyList<PatchOperation> patchOperations,
                                            CancellationToken cancellationToken)
        {
            if (!CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.IdField, out string id) || string.IsNullOrEmpty(id))
            {
                throw new InvalidOperationException($"A pending outbox document is missing its 'id'; cannot stamp it {stampKind}.");
            }

            PartitionKey partitionKey = RecoverPartitionKey(document, partitionKeyPath);

            return monitoredContainer.PatchItemAsync<JsonElement>(id, partitionKey, patchOperations, requestOptions: null, cancellationToken: cancellationToken);
        }

        // Recovers the document's partition key by reading the value(s) the document carries at the container's declared
        // partition-key path (single or hierarchical) and building a PartitionKey that preserves each value's JSON kind
        // (string/number/bool/null), so the delivered/TTL patch lands in the SAME logical partition the document lives
        // in. A path segment may be nested (e.g. "/tenant/id"), navigated object-by-object; a missing/array-valued
        // intermediate yields a JSON-null component, mirroring how the document was stamped on write.
        // internal (not private) so the standalone relay host keys its #361 poison counter on the SAME recovery the
        // stamp targets rather than re-implementing it — one derivation, so the counter and the patch cannot diverge.
        // The assembly exposes internals to the host (same assembly) and the test project. No behavior change.
        internal static PartitionKey RecoverPartitionKey(JsonElement document, IReadOnlyList<string> partitionKeyPath)
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
