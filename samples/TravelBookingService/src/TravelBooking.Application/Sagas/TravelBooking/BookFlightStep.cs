using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Sagas.TravelBooking.Commands;

namespace TravelBooking.Application.Sagas.TravelBooking
{
    public class BookFlightStep : IMessageHandler<BookFlightCommand>
    {
        public BookFlightStep()
        {
        }

        public async Task Handle(BookFlightCommand message, IMessageHandlerContext context)
        {
            var flight = message.SagaData.Flight;
            if (flight != null)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("Booking Flight");
                    Console.ResetColor();
                }

                if (DateTime.UtcNow.Second >= 1 && DateTime.UtcNow.Second <= 15)
                {
                    throw new Exception($"Fake business Logic not satisfied for booking flight.");
                }

                if (DateTime.UtcNow.Second >= 43 && DateTime.UtcNow.Second <= 45)
                {
                    throw new Exception($"Fake exception thrown in book flight saga action.");
                }

                // let's pretend we booked something
                flight.ReservationId = Guid.NewGuid();
            }
            await Task.CompletedTask;
        }
    }
}
