using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Context;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageDispatcher
    {
        Task Compensate(InboundBrokeredMessage inboundBrokeredMessage, CompensationRoutingContext routingContext, TransactionContext transactionContext = null);
        Task Send<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null) where TMessage : ICommand;
        Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null) where TMessage : ICommand;
        Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null) where TMessage : IEvent;
        Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null) where TMessage : IEvent;
        Task Publish<TMessage>(IEnumerable<TMessage> messages, TransactionContext transactionContext = null) where TMessage : IEvent;
        Task ReplyTo(InboundBrokeredMessage inboundBrokeredMessage, ReplyToRoutingContext routingContext, TransactionContext transactionContext = null);
        Task ReplyToRequester<TMessage>(TMessage message, TransactionContext transactionContext = null) where TMessage : ICommand;
        Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext = null);
        Task Forward(InboundBrokeredMessage inboundBrokeredMessage, IContainRoutingContext routingContext, TransactionContext transactionContext = null);
    }
}
