using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The handle-gated outbound router that makes the Cosmos document tier participation-gated AT ROUTING, mirroring the
    /// participation gating the <see cref="DocumentTierBatchLifecycleBehavior{TMessage}"/> already applies to BEHAVIOR
    /// (ADR-0008: "a command type without a registration bypasses the document tier entirely"). The Cosmos DI
    /// registration globally replaces <see cref="IRouteBrokeredMessages"/>, but only PARTICIPANTS open a document-tier
    /// batch (which sets the surface handle); a non-participant command whose handler Send/Publishes would otherwise route
    /// straight to the Cosmos outbox and throw on the null handle. This decorator closes that gap.
    /// </summary>
    /// <remarks>
    /// Closed-by-construction: the gate key is the surface's active atomic-write handle, which is set ONLY inside a
    /// participant's open batch scope (and cleared in the behavior's <c>finally</c>). When the handle is null — every
    /// non-participant dispatch, and every dispatch after a participant's batch scope closes — routing delegates to the
    /// inner default arm, which the Cosmos DI registration constructs DIRECTLY over the always-registered
    /// <c>IMessagingInfrastructureProvider</c> (a plain broker router), exactly as if Cosmos were not installed. Because
    /// the inner default never depends on the Cosmos outbox, a non-participant dispatch can never reach the Cosmos
    /// outbox's null-handle requirement regardless of registration order: the malformed "non-participant routes to the
    /// Cosmos outbox" class is unrepresentable, not merely unobserved (#238, ADR-0008). The
    /// <see cref="CosmosBrokeredMessageOutbox"/> null-handle throw is left intact as a hard wiring-error guard; this
    /// decorator ensures non-participants never reach it.
    /// </remarks>
    internal sealed class HandleGatedOutboxRouter : IRouteBrokeredMessages
    {
        private readonly IRouteBrokeredMessages _cosmosOutboxRouter;
        private readonly IRouteBrokeredMessages _innerDefaultRouter;
        private readonly IDocumentTierReliabilitySurface _surface;

        public HandleGatedOutboxRouter(IRouteBrokeredMessages cosmosOutboxRouter,
                                       IRouteBrokeredMessages innerDefaultRouter,
                                       IDocumentTierReliabilitySurface surface)
        {
            _cosmosOutboxRouter = cosmosOutboxRouter ?? throw new ArgumentNullException(nameof(cosmosOutboxRouter));
            _innerDefaultRouter = innerDefaultRouter ?? throw new ArgumentNullException(nameof(innerDefaultRouter));
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        }

        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
            => SelectRouter().Route(outboundBrokeredMessage, transactionContext);

        public Task Route(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, string infrastructureType = "")
            => SelectRouter().Route(outboundBrokeredMessages, transactionContext, infrastructureType);

        // An active handle means an open participant batch — route through the Cosmos outbox so the outbound contributes
        // to the framework-owned batch (unchanged participant behavior). A null handle means a non-participant dispatch
        // (or a dispatch after the batch scope closed) — route through the core default, never the Cosmos outbox.
        private IRouteBrokeredMessages SelectRouter()
            => _surface.CurrentHandle is null ? _innerDefaultRouter : _cosmosOutboxRouter;
    }
}
