using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // A test IMessagingInfrastructure registered as the application's ONLY infrastructure, so it is the default the
    // MessagingInfrastructureProvider resolves both for the relay's GetDispatcher(infra) publish and for a
    // non-participant command's broker-direct route. Its Type matches the infrastructure type the suite stamps on
    // outbound messages so GetDispatcher resolves it by name as well as by default. Its DispatchInfrastructure records
    // every published OutboundBrokeredMessage into a thread-safe sink; the receive/path members are inert stubs
    // sufficient for the provider to construct (the suite never starts a receiver pump — delivery is driven directly
    // through IReceivedMessageDispatcher).
    public sealed class CapturingInfrastructure : IMessagingInfrastructure
    {
        // The infrastructure type the suite stamps on inbound application properties so the reconstructed outbound
        // message carries it; GetDispatcher(InfrastructureType) then resolves this infrastructure by name. It is also
        // the only infrastructure registered, so a GetDispatcher(null/empty) (the default-infrastructure path) lands
        // here too.
        public const string InfrastructureType = "chatter-cosmos-it-capture";

        private readonly CapturingDispatcher _dispatcher = new CapturingDispatcher();

        public string Type => InfrastructureType;

        public IMessagingInfrastructureReceiver ReceiveInfrastructure
            => throw new NotSupportedException("The capturing infrastructure does not receive; the suite drives delivery through IReceivedMessageDispatcher.");

        public IMessagingInfrastructureDispatcher DispatchInfrastructure => _dispatcher;

        public IBrokeredMessagePathBuilder PathBuilder => PassthroughPathBuilder.Instance;

        // The thread-safe publish ledger: every OutboundBrokeredMessage the relay (or a non-participant broker-direct
        // route) dispatches, in dispatch order.
        public IReadOnlyList<OutboundBrokeredMessage> Published => _dispatcher.Published;

        // Bounded wait until at least minCount messages have been captured, returning the captured snapshot. Returns
        // the last observed snapshot (which may hold fewer than minCount) if the timeout elapses first — callers assert
        // on the returned count so a never-reached threshold fails fast rather than hanging CI. A polling wait (not a
        // fixed sleep) so it returns as soon as the threshold is met.
        public async Task<IReadOnlyList<OutboundBrokeredMessage>> WaitForPublishedAsync(int minCount, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                IReadOnlyList<OutboundBrokeredMessage> snapshot = _dispatcher.Published;
                if (snapshot.Count >= minCount)
                {
                    return snapshot;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }

            return _dispatcher.Published;
        }

        private sealed class CapturingDispatcher : IMessagingInfrastructureDispatcher
        {
            private readonly ConcurrentQueue<OutboundBrokeredMessage> _published = new ConcurrentQueue<OutboundBrokeredMessage>();

            public IReadOnlyList<OutboundBrokeredMessage> Published => _published.ToArray();

            public Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
            {
                foreach (OutboundBrokeredMessage brokeredMessage in brokeredMessages)
                {
                    _published.Enqueue(brokeredMessage);
                }

                return Task.CompletedTask;
            }

            public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
            {
                _published.Enqueue(brokeredMessage);
                return Task.CompletedTask;
            }
        }

        // A path builder that returns the supplied paths verbatim — the suite addresses destinations by their raw
        // names, so no broker-specific path mangling is needed.
        private sealed class PassthroughPathBuilder : IBrokeredMessagePathBuilder
        {
            public static readonly PassthroughPathBuilder Instance = new PassthroughPathBuilder();

            public string GetMessageSendingPath(string messageSendingPath) => messageSendingPath;

            public string GetMessageReceivingRulePath(string messageSendingPath, string messageReceiverPath, string ruleName) => messageReceiverPath;

            public string GetMessageReceivingPath(string messageSendingPath, string messageReceiverPath) => messageReceiverPath;
        }
    }
}
