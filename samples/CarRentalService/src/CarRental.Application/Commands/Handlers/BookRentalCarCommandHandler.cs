using CarRental.Application.IntegrationEvents;
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Chatter.CQRS.Events;

namespace CarRental.Application.Commands.Handlers
{
    public class BookRentalCarCommandHandler : IMessageHandler<BookRentalCarCommand>
    {
        private readonly IBrokeredMessageInfrastructureDispatcher brokeredMessageInfrastructureDispatcher;

        public BookRentalCarCommandHandler(IBrokeredMessageInfrastructureDispatcher brokeredMessageInfrastructureDispatcher)
        {
            this.brokeredMessageInfrastructureDispatcher = brokeredMessageInfrastructureDispatcher;
        }

        public async Task Handle(BookRentalCarCommand message, IMessageHandlerContext context)
        {
            TransactionContext transactionContext = null;
            lock (Console.Out)
            {
                if (context is IMessageBrokerContext messageBrokerContext)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Received '{message.GetType().Name}' event from message broker. Message Id: '{messageBrokerContext.BrokeredMessage.MessageId}', Subscription: '{messageBrokerContext.BrokeredMessage.MessageReceiverPath}'");
                    transactionContext = messageBrokerContext.GetTransactionContext();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Received '{message.GetType().Name}' event from local dispatcher");
                }

                Console.WriteLine($"Command Data: {JsonConvert.SerializeObject(message)}");

            }
            //create new rental reservation Id
            message.Car.ReservationId = Guid.NewGuid();
            //save the CarRental aggregate to persistance
            //saving the aggregate would typically create and dispastch the domain events, but since no aggregate exsits in this example, creating and firing domain event below
            var e = new RentalCarBookedEvent()
            {
                Id = message.Car.Id,
                ReservationId = message.Car.ReservationId
            };

            lock (Console.Out)
            {
                Console.WriteLine($"Persisted Car Rental aggregate with reservation id: {message.Car.ReservationId}");
            }

            await Publish(e, transactionContext);

            lock (Console.Out)
            {
                Console.WriteLine($"Dispatched domain event: {JsonConvert.SerializeObject(e)}");
                Console.ResetColor();
            }
        }

        private Task Publish<TMessage>(TMessage @event, TransactionContext transactionContext) where TMessage : IEvent
        {
            var outbound = new OutboundBrokeredMessage(@event, "book-trip-saga/rental-car-booked", new JsonBodyConverter());
            return this.brokeredMessageInfrastructureDispatcher.Dispatch(outbound, transactionContext);
        }
    }
}
