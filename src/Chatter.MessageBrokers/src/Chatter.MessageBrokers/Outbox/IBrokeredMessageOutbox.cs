using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Outbox
{
    public interface IBrokeredMessageOutbox
    {
        bool IsOutboxEnabled { get; }
        Task Send(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext);
    }
}
