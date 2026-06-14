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
    /// The outermost document-tier pipeline behavior — the document-tier sibling of <c>UnitOfWorkBehavior</c>. It owns
    /// the batch lifecycle: resolve the partition key via the app-registered <see cref="PartitionKeyResolver"/>, open a
    /// <see cref="TransactionalBatch"/> on the bound document container for that partition, expose the doc-tier
    /// atomic-write handle on the document-tier surface, call <c>next()</c>, then execute the batch once.
    /// </summary>
    /// <remarks>
    /// #218 shell only — this contributes NO inbox marker and NO outbox op (those are #219/#220); it leaves clean
    /// seams. The single batch-execute is GUARDED: an empty batch (zero staged ops) does NOT call the Cosmos transport,
    /// so unit tests need no live Cosmos. Must register OUTERMOST (first WithBehavior registration; the pipeline
    /// reverses behaviors so the first-registered is the outermost).
    /// </remarks>
    public sealed class DocumentTierBatchLifecycleBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
    {
        private readonly DocumentContainer _documentContainer;
        private readonly PartitionKeyResolver _partitionKeyResolver;
        private readonly DocumentTierReliabilitySurface _surface;

        public DocumentTierBatchLifecycleBehavior(DocumentContainer documentContainer, PartitionKeyResolver partitionKeyResolver, DocumentTierReliabilitySurface surface)
        {
            _documentContainer = documentContainer ?? throw new ArgumentNullException(nameof(documentContainer));
            _partitionKeyResolver = partitionKeyResolver ?? throw new ArgumentNullException(nameof(partitionKeyResolver));
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        }

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            InboundBrokeredMessage inboundBrokeredMessage = messageHandlerContext.GetInboundBrokeredMessage();
            PartitionKey partitionKey = _partitionKeyResolver.Resolve(inboundBrokeredMessage);

            Container container = _documentContainer.Container;
            TransactionalBatch batch = container.CreateTransactionalBatch(partitionKey);

            var handle = new CosmosAtomicWriteHandle(container, batch, partitionKey, _partitionKeyResolver.PartitionKeyPath);
            _surface.CurrentHandle = handle;
            try
            {
                await next();

                // INVARIANT: the single batch-execute is the only commit point. Skip it when no op was staged so an
                // empty batch never calls the Cosmos transport (#218 stages nothing; #219/#220 stage ops).
                if (handle.StagedOperationCount > 0)
                {
                    await batch.ExecuteAsync(messageHandlerContext?.CancellationToken ?? default);
                }
            }
            finally
            {
                _surface.CurrentHandle = null;
            }
        }
    }
}
