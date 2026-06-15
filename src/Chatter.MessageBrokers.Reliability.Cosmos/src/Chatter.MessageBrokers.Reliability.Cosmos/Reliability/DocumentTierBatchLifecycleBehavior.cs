using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Azure.Cosmos;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The outermost document-tier pipeline behavior — the document-tier sibling of <c>UnitOfWorkBehavior</c>. It is
    /// PARTICIPATION-GATED via the <see cref="DocumentReliabilityRegistry"/>: only a command TYPE with a registration
    /// (the registry is a positive allowlist) opens a batch; every other command bare-passes-through to <c>next()</c>
    /// with no resolver call and no batch (ADR-0008). For a participant it resolves the partition key via the
    /// registration's Try/nullable resolver, derives+caches the registration's container via the
    /// <see cref="CosmosContainerFactory"/>, opens a <see cref="TransactionalBatch"/> on that container for the
    /// partition, exposes the doc-tier atomic-write handle on the document-tier surface, calls <c>next()</c>, then
    /// executes the batch once.
    /// </summary>
    /// <remarks>
    /// Three control paths: (1) non-participant — bare <c>next()</c>; (2) participant whose resolver returns a null
    /// partition key — bare <c>next()</c>, no batch opened; (3) participant with a partition — open batch, run the
    /// framework-owned lifecycle. The single batch-execute is GUARDED: an empty batch (zero staged ops) does NOT call
    /// the Cosmos transport, so unit tests need no live Cosmos. Must register OUTERMOST (first WithBehavior
    /// registration; the pipeline reverses behaviors so the first-registered is the outermost).
    /// </remarks>
    public sealed class DocumentTierBatchLifecycleBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
    {
        private readonly DocumentReliabilityRegistry _registry;
        private readonly CosmosContainerFactory _containerFactory;
        private readonly DocumentTierReliabilitySurface _surface;

        public DocumentTierBatchLifecycleBehavior(DocumentReliabilityRegistry registry, CosmosContainerFactory containerFactory, DocumentTierReliabilitySurface surface)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _containerFactory = containerFactory ?? throw new ArgumentNullException(nameof(containerFactory));
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        }

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            // Participation = has a registration. A non-participant is a cheap registry miss and never reaches the
            // resolver or the container factory — no batch, no resolver call.
            if (!_registry.TryGet(typeof(TMessage), out DocumentReliabilityRegistration registration))
            {
                await next();
                return;
            }

            // The resolver is only ever invoked for participants. It is Try/nullable: a null partition key means "no
            // resolvable partition for this message" — a legitimate no-op for a participant, so no batch is opened.
            InboundBrokeredMessage inboundBrokeredMessage = messageHandlerContext.GetInboundBrokeredMessage();
            PartitionKey? partitionKey = registration.Resolver(inboundBrokeredMessage);
            if (partitionKey is null)
            {
                await next();
                return;
            }

            Container container = _containerFactory.GetDocumentContainer(registration);
            TransactionalBatch batch = container.CreateTransactionalBatch(partitionKey.Value);

            var handle = new CosmosAtomicWriteHandle(container, batch, partitionKey.Value, registration.PartitionKeyPath);
            _surface.CurrentHandle = handle;
            try
            {
                await next();

                // INVARIANT: the single batch-execute is the only commit point. Skip it when no op was staged so an
                // empty batch never calls the Cosmos transport.
                if (handle.StagedOperationCount > 0)
                {
                    TransactionalBatchResponse response = await batch.ExecuteAsync(messageHandlerContext?.CancellationToken ?? default);
                    InspectBatchResponse(response);
                }
            }
            finally
            {
                _surface.CurrentHandle = null;
            }
        }

        /// <summary>
        /// Inspects the <see cref="TransactionalBatchResponse"/> from the single batch-execute. Because the batch is
        /// all-or-nothing, a non-success batch means no aggregate, outbox, or marker write committed; the message must
        /// NOT be acked, so this throws so the transport redelivers. This is the framework-owned response-inspection
        /// seam: #220 layers confirmed-duplicate handling here (a 409 on the inbox-marker op is a swallow-no-throw), so
        /// the per-op detail of <paramref name="response"/> is examined inside this single method rather than at the
        /// call site — #220 can distinguish the marker 409 without rewriting the execute path.
        /// </summary>
        private static void InspectBatchResponse(TransactionalBatchResponse response)
        {
            // INVARIANT: a forced aggregate ETag/412 (or any non-success op) surfaces as IsSuccessStatusCode == false on
            // the batch; throwing here is what prevents an ack when the writes did not commit.
            if (response is null || !response.IsSuccessStatusCode)
            {
                throw new CosmosBatchExecutionException(response);
            }
        }
    }
}
