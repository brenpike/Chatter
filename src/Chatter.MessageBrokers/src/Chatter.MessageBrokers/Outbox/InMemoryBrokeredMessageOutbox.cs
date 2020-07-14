using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Outbox
{
    public class InMemoryBrokeredMessageOutbox : IBrokeredMessageOutbox
    {
        private readonly ConcurrentDictionary<string, OutboxMessage> _outbox;

        public InMemoryBrokeredMessageOutbox()
        {
            _outbox = new ConcurrentDictionary<string, OutboxMessage>();
        }

        public bool IsOutboxEnabled => false;

        public Task Send(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {


            throw new NotImplementedException();
        }
    }
}
