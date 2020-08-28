using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Routing.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Context
{
    public interface IMessageBrokerRoutingContext
    {
        Task Send<TMessage>(TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand;
        Task Send<TMessage>(TMessage message, SendOptions options = null) where TMessage : ICommand;
        Task Publish<TMessage>(TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent;
        Task Publish<TMessage>(TMessage message, PublishOptions options = null) where TMessage : IEvent;
    }
}
