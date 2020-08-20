using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Routing.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageDispatcher : IExternalDispatcher
    {
        Task Compensate(InboundBrokeredMessage inboundBrokeredMessage, CompensationRoutingContext routingContext, TransactionContext transactionContext = null);
        Task Send<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand;
        Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand;
        Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent;
        Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent;
        Task ReplyTo(InboundBrokeredMessage inboundBrokeredMessage, ReplyToRoutingContext routingContext, TransactionContext transactionContext = null, ReplyToOptions options = null);
        Task ReplyToRequester<TMessage>(TMessage message, TransactionContext transactionContext = null) where TMessage : ICommand;
        Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext = null, ForwardingOptions options = null);
        Task Forward(InboundBrokeredMessage inboundBrokeredMessage, IContainRoutingContext routingContext, TransactionContext transactionContext = null, ForwardingOptions options = null);
    }
}
