using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Sagas.TravelBooking.Commands;

namespace TravelBooking.Application.Sagas.TravelBooking
{
    public class CancelRentalCarStep : IMessageHandler<CancelCarRentalCommand>
    {
        public CancelRentalCarStep()
        {
        }

        public async Task Handle(CancelCarRentalCommand message, IMessageHandlerContext context)
        {
            var car = message.SagaData.Car;
            if (car != null &&
                car.ReservationId != Guid.Empty)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Cancelling Rental Car");
                    Console.ResetColor();
                }

                if (DateTime.UtcNow.Second >= 38 && DateTime.UtcNow.Second <= 40)
                {
                    throw new Exception($"Fake exception thrown in cancel rental car saga action.");
                }

                // reset the id
                car.ReservationId = Guid.Empty;
            }
            await Task.CompletedTask;
        }
    }
}
