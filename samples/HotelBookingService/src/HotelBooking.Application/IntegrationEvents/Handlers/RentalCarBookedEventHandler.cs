using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace HotelBooking.Application.IntegrationEvents.Handlers
{
    public class RentalCarBookedEventHandler : IMessageHandler<RentalCarBookedEvent>
    {
        public Task Handle(RentalCarBookedEvent message, IMessageHandlerContext context)
        {
            lock (Console.Out)
            {
                if (context is IMessageBrokerContext messageBrokerContext)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"Received '{message.GetType().Name}' event from message broker. Message Id: '{messageBrokerContext.BrokeredMessage.MessageId}', Subscription: '{messageBrokerContext.BrokeredMessage.MessageReceiverPath}'");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.WriteLine($"Received '{message.GetType().Name}' event from local dispatcher");
                }

                Console.WriteLine($"Event Data: {JsonSerializer.Serialize(message)}");
                Console.ResetColor();
            }

            return Task.CompletedTask;
        }
    }
}
