using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace CarRental.Application.Events.Handlers
{
    public class CarRentalAggregateChangedEventHandler : IMessageHandler<CarRentalAggregateChangedEvent>
    {
        public Task Handle(CarRentalAggregateChangedEvent message, IMessageHandlerContext context)
        {
            lock (Console.Out)
            {
                var changeContext = context.ChangeNotificationContext<CarRentalAggregateChangedEvent>();
                var oldValue = changeContext.OldValue is null ? "" : JsonSerializer.Serialize(changeContext.OldValue);
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"-----------------------------------------------------------------------");
                Console.WriteLine($"Sql table was changed. Type: '{changeContext.ChangeType}'");
                Console.WriteLine($"  --> New Value: {JsonSerializer.Serialize(message)}");
                Console.WriteLine($"  --> Old Value: {oldValue}");
                Console.WriteLine($"-----------------------------------------------------------------------");
                Console.WriteLine();
                Console.ResetColor();
            }

            return Task.CompletedTask;
        }
    }
}
