using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public interface IBrokeredMessageOutbox
    {
        Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext, CancellationToken cancellationToken = default)
             => SendToOutbox(new[] { outboundBrokeredMessage }, transactionContext, cancellationToken);
        Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default);
    }
}
