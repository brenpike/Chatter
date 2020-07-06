using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Sagas.TravelBooking.Commands;

namespace TravelBooking.Application.Sagas.TravelBooking
{
    public class BookHotelStep : IMessageHandler<BookHotelCommand>
    {
        public BookHotelStep()
        {
        }

        public async Task Handle(BookHotelCommand message, IMessageHandlerContext context)
        {
            var hotel = message.SagaData.Hotel;
            if (hotel != null)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.WriteLine("Booking Hotel");
                    Console.ResetColor();
                }

                if (DateTime.UtcNow.Second >= 57 && DateTime.UtcNow.Second <= 59)
                {
                    throw new Exception($"Fake exception thrown in book hotel saga action.");
                }

                // let's pretend we booked something
                hotel.ReservationId = Guid.NewGuid();
            }
            await Task.CompletedTask;
        }
    }
}
