using CarRental.Application.Services;
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using Newtonsoft.Json;
using Samples.SharedKernel.Interfaces;
using System;
using System.Threading.Tasks;

namespace CarRental.Application.Commands.Handlers
{
    public class BookRentalCarCommandHandler : IMessageHandler<BookRentalCarCommand>
    {
        private readonly IRepository<Domain.Aggregates.CarRental, Guid> _repository;
        private readonly IEventMapper _eventMapper;

        public BookRentalCarCommandHandler(IRepository<Domain.Aggregates.CarRental, Guid> repository, IEventMapper eventMapper)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _eventMapper = eventMapper ?? throw new ArgumentNullException(nameof(eventMapper));
        }

        public async Task Handle(BookRentalCarCommand message, IMessageHandlerContext context)
        {
            lock (Console.Out)
            {
                if (context is IMessageBrokerContext messageBrokerContext)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"Received '{message.GetType().Name}' command from message broker. Message Id: '{messageBrokerContext.BrokeredMessage.MessageId}', Subscription: '{messageBrokerContext.BrokeredMessage.MessageReceiverPath}'");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Received '{message.GetType().Name}' command from local dispatcher");
                }
                Console.WriteLine($"Command Data: {JsonConvert.SerializeObject(message)}");
                Console.ResetColor();
            }

            var carRental = new Domain.Aggregates.CarRental(); //TODO: just an example - likely more properties would be passed in via ctor
            var reservationId = carRental.Reserve(message.Car.Airport, message.Car.Vendor, message.Car.From, message.Car.Until);
            await _repository.AddAsync(carRental);

            var integrationEvents = _eventMapper.MapAll(carRental.DomainEvents);

            //throw new Exception("without using the outbox, we're in a bad state. aggregate has been saved, integration events will never be published.");

            foreach (var ie in integrationEvents)
            {
                var options = new PublishOptions()
                {
                    MessageId = Guid.NewGuid().ToString()
                };

                await context.Publish(ie, options);

                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Dispatched domain event: {JsonConvert.SerializeObject(ie)}");
                    Console.ResetColor();
                }
            }
        }
    }
}
