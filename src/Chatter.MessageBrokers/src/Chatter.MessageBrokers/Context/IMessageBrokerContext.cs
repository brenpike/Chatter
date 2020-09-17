using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Used to pass contextual information of a <see cref="BrokeredMessageReceiver{TMessage}"/> to a <see cref="IMessageHandler{TMessage}"/>
    /// </summary>
    public interface IMessageBrokerContext : IMessageHandlerContext
    {
        /// <summary>
        /// The message received by a <see cref="BrokeredMessageReceiver{TMessage}"/>
        /// </summary>
        InboundBrokeredMessage BrokeredMessage { get; }

        Task Publish<TMessage>(TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent;
        Task Publish<TMessage>(TMessage message, PublishOptions options = null) where TMessage : IEvent;
        Task Send<TMessage>(TMessage message, SendOptions options = null) where TMessage : ICommand;
        Task Send<TMessage>(TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand;
    }
}
