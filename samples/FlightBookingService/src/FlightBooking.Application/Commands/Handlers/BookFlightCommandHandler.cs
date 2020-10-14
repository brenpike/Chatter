using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;

namespace FlightBooking.Application.Commands.Handlers
{
    public class BookFlightCommandHandler : IMessageHandler<BookFlightCommand>
    {
        public Task Handle(BookFlightCommand message, IMessageHandlerContext context)
        {
            var flight = message;
            if (flight != null)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("Booking Flight");
                    Console.ResetColor();
                }

                //throw new Exception($"Fake business Logic not satisfied for booking flight.");
                //if (DateTime.UtcNow.Second >= 1 && DateTime.UtcNow.Second <= 15)
                //{
                //    throw new Exception($"Fake business Logic not satisfied for booking flight.");
                //}

                //if (DateTime.UtcNow.Second >= 43 && DateTime.UtcNow.Second <= 45)
                //{
                //    throw new Exception($"Fake exception thrown in book flight saga action.");
                //}

                // let's pretend we booked something
                flight.ReservationId = Guid.NewGuid();
            }
            return Task.CompletedTask;
        }
    }
}
