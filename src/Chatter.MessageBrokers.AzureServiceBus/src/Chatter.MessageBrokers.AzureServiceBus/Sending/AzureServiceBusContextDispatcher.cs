using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    public class AzureServiceBusContextDispatcher : IAzureServiceBusContextDispatcher
    {
        private readonly IMessageBrokerContext _context;

        public AzureServiceBusContextDispatcher(IMessageBrokerContext context)
        {
            _context = context;
            _context?.BrokeredMessage?.UseMessagingInfrastructure(it => it.AzureServiceBus());
        }

        public Task Forward(string forwardDestination)
            => _context?.Forward(forwardDestination);

        public Task Publish<TMessage>(TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent
            => _context?.Publish(message, destinationPath, options);

        public Task Publish<TMessage>(TMessage message, PublishOptions options = null) where TMessage : IEvent
            => _context?.Publish(message, options);

        public Task Publish<TMessage>(IEnumerable<TMessage> messages, PublishOptions options = null) where TMessage : IEvent
            => _context?.Publish(messages, options);

        public Task Send<TMessage>(TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand
            => _context?.Send(message, destinationPath, options);

        public Task Send<TMessage>(TMessage message, SendOptions options = null) where TMessage : ICommand
            => _context?.Send(message, options);
    }
}
