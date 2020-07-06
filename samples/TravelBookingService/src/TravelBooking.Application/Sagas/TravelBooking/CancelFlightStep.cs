using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Sagas.TravelBooking.Commands;

namespace TravelBooking.Application.Sagas.TravelBooking
{
    public class CancelFlightStep : IMessageHandler<CancelFlightBookingCommand>
    {
        public CancelFlightStep()
        {
        }

        public async Task Handle(CancelFlightBookingCommand message, IMessageHandlerContext context)
        {
            var flight = message.SagaData.Flight;
            if (flight != null &&
                flight.ReservationId != Guid.Empty)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Cancelling Flight");
                    Console.ResetColor();
                }

                if (DateTime.UtcNow.Second >= 2 && DateTime.UtcNow.Second <= 4)
                {
                    throw new Exception($"Fake exception thrown in cancel flight saga action.");
                }

                // reset the id
                flight.ReservationId = Guid.Empty;
            }
            await Task.CompletedTask;
        }
    }
}
