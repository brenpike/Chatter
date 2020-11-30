using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace CarRental.Application.Events.Handlers
{
    public class OutboxChangedEventHandler : IMessageHandler<OutboxChangedEvent>
    {
        public Task Handle(OutboxChangedEvent message, IMessageHandlerContext context)
        {
            lock (Console.Out)
            {
                var changeContext = context.ChangeNotificationContext<OutboxChangedEvent>();
                var oldValue = changeContext.OldValue is null ? "" : JsonSerializer.Serialize(changeContext.OldValue);
                Console.ForegroundColor = ConsoleColor.Yellow;
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
