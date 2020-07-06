using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Sagas.TravelBooking.Commands;

namespace TravelBooking.Application.Sagas.TravelBooking
{
    public class ResultStep : IMessageHandler<TravelBookingResultCommand>
    {
        public ResultStep()
        {
        }

        public async Task Handle(TravelBookingResultCommand message, IMessageHandlerContext context)
        {
            var result = message.SagaData;
            lock (Console.Out)
            {
                var inbound = context.GetInboundBrokeredMessage();
                Console.ForegroundColor = !inbound.IsSuccess
                    ? ConsoleColor.Magenta
                    : ConsoleColor.Green;

                foreach (var prop in inbound.ApplicationProperties)
                {
                    Console.WriteLine("{0}={1},", prop.Key, prop.Value);
                }

                Console.WriteLine(
                    "{0}\n",
                    result);

                Console.ResetColor();
            }
            await Task.CompletedTask;
        }
    }
}
