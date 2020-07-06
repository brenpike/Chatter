using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Sagas.TravelBooking.Commands;

namespace TravelBooking.Application.Sagas.TravelBooking
{
    public class BookRentalCarStep : IMessageHandler<BookCarRentalCommand>
    {
        public BookRentalCarStep()
        {
        }

        public async Task Handle(BookCarRentalCommand message, IMessageHandlerContext context)
        {
            var car = message.SagaData.Car;
            if (car != null)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Booking Rental Car");
                    Console.ResetColor();
                }

                if (DateTime.UtcNow.Second >= 32 && DateTime.UtcNow.Second <= 35)
                {
                    throw new Exception($"Fake exception thrown in 'Book Car Rental' saga action.");
                }

                //pretend to reserve a car rental
                car.ReservationId = Guid.NewGuid();
            }
            await Task.CompletedTask;
        }
    }
}
