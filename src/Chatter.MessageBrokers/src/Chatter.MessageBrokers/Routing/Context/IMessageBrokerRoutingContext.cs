using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Context
{
    public interface IMessageBrokerRoutingContext
    {
        //TODO: add sendinteral and publish internal
        Task Compensate<TMessage>(TMessage message, string compensationDescription, string compensationDetails) where TMessage : ICommand;
        Task Compensate<TMessage>(TMessage message, string destinationPath, string compensationDescription, string compensationDetails) where TMessage : ICommand;
        Task Compensate();

        Task Send<TMessage>(TMessage message, string destinationPath) where TMessage : ICommand;
        Task Send<TMessage>(TMessage message) where TMessage : ICommand;

        Task Publish<TMessage>(TMessage message, string destinationPath) where TMessage : IEvent;
        Task Publish<TMessage>(TMessage message) where TMessage : IEvent;
        Task Publish<TMessage>(IEnumerable<TMessage> messages) where TMessage : IEvent;

        Task ReplyTo();
        Task ReplyToRequester<TMessage>(TMessage message) where TMessage : ICommand;

        Task Forward<TRoutingContext>() where TRoutingContext : IContainRoutingContext;
    }
}
