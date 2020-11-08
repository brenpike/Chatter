using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;

namespace HotelBooking.Application.Commands.Handlers
{
    public class BookHotelCommandHandler : IMessageHandler<BookHotelCommand>
    {
        public Task Handle(BookHotelCommand message, IMessageHandlerContext context)
        {
            var hotel = message;
            if (hotel != null)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.WriteLine("Booking Hotel");
                    Console.ResetColor();
                }

                //if (DateTime.UtcNow.Second >= 57 && DateTime.UtcNow.Second <= 59)
                //{
                //    throw new Exception($"Fake exception thrown in book hotel command handler.");
                //}

                // let's pretend we booked something
                hotel.ReservationId = Guid.NewGuid();
            }
            return Task.CompletedTask;
        }
    }
}
