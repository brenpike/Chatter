using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace CarRental.Application.Commands.Handlers
{
    public class CancelRentalCarCommandHandler : IMessageHandler<CancelRentalCarCommand>
    {
        public Task Handle(CancelRentalCarCommand message, IMessageHandlerContext context)
        {
            var car = message.Car;
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
            return Task.CompletedTask;
        }
    }
}
