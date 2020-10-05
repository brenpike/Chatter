using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;

namespace HotelBooking.Application.Commands.Handlers
{
    public class CancelHotelBookingCommandHandler : IMessageHandler<CancelHotelBookingCommand>
    {
        public Task Handle(CancelHotelBookingCommand message, IMessageHandlerContext context)
        {
            var hotel = message;
            if (hotel != null &&
                hotel.ReservationId != Guid.Empty)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Cancelling Hotel");
                    Console.ResetColor();
                }

                //if (DateTime.UtcNow.Second >= 18 && DateTime.UtcNow.Second <= 20)
                //{
                //    throw new Exception($"Fake exception thrown in cancel hotel booking handler.");
                //}

                // reset the id
                hotel.ReservationId = Guid.Empty;
            }
            return Task.CompletedTask;
        }
    }
}
