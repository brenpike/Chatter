using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace CarRental.Application.Events.Handlers
{
    public class OutboxChangedEventHandler : IMessageHandler<OutboxChangedEvent>
    {
        public Task Handle(OutboxChangedEvent message, IMessageHandlerContext context)
        {
            lock (Console.Out)
            {
                //var changeContext = context.ChangeNotificationContext<OutboxChangedEvent>();
                //var oldValue = changeContext?.OldValue is null ? "" : JsonSerializer.Serialize(changeContext.OldValue);
                context.Container.TryGet<ReceivedMessage>(out var msg);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"-----------------------------------------------------------------------");
                Console.WriteLine($"{JsonConvert.SerializeObject(msg)}");
                Console.WriteLine($"-----------------------------------------------------------------------");
                Console.WriteLine();
                Console.ResetColor();
            }

            var po = new PublishOptions();
            po.SetCorrelationId("ADDDDDASDSAdAsdsdASDSD");

            //return context.Publish(message, po);
            //return context.SqlServiceBroker().Publish(message, po);

            //var test = new RentalCarBookedEvent
            //{
            //    Id = Guid.NewGuid(),
            //    ReservationId = Guid.NewGuid()
            //};
            //po.ContentType = "application/json";
            //return context.AzureServiceBus().Publish(test, po);

            return Task.CompletedTask;
        }
    }
}
