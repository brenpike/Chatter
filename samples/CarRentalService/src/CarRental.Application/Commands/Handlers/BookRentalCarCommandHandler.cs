using CarRental.Application.IntegrationEvents;
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace CarRental.Application.Commands.Handlers
{
    public class BookRentalCarCommandHandler : IMessageHandler<BookRentalCarCommand>
    {
        private readonly IBrokeredMessageDispatcher _brokeredMessageDispatcher;

        public BookRentalCarCommandHandler(IBrokeredMessageDispatcher brokeredMessageDispatcher)
        {
            _brokeredMessageDispatcher = brokeredMessageDispatcher;
        }

        public async Task Handle(BookRentalCarCommand message, IMessageHandlerContext context)
        {
            lock (Console.Out)
            {
                if (context is IMessageBrokerContext messageBrokerContext)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Received '{message.GetType().Name}' command from message broker. Message Id: '{messageBrokerContext.BrokeredMessage.MessageId}', Subscription: '{messageBrokerContext.BrokeredMessage.MessageReceiverPath}'");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Received '{message.GetType().Name}' command from local dispatcher");
                }
                Console.WriteLine($"Command Data: {JsonConvert.SerializeObject(message)}");
            }

            //create new rental reservation Id
            message.Car.ReservationId = Guid.NewGuid();

            lock (Console.Out)
            {
                Console.WriteLine($"Persisted Car Rental aggregate with reservation id: {message.Car.ReservationId}");
            }

            //save the CarRental aggregate to persistance
            //saving the aggregate would typically create and dispastch the domain events, but since no aggregate exists in this example
            //creating and firing domain event below
            var e = new RentalCarBookedEvent()
            {
                Id = message.Car.Id,
                ReservationId = message.Car.ReservationId
            };

            //optional
            var publishOptions = new PublishOptions()
            {
                MessageId = Guid.NewGuid().ToString()
            };

            await _brokeredMessageDispatcher.Publish(e, options: publishOptions);

            lock (Console.Out)
            {
                Console.WriteLine($"Dispatched domain event: {JsonConvert.SerializeObject(e)}");
                Console.ResetColor();
            }
        }
    }
}
