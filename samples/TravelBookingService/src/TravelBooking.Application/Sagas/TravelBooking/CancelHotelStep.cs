using Chatter.CQRS;
using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Sagas.TravelBooking.Commands;

namespace TravelBooking.Application.Sagas.TravelBooking
{
    public class CancelHotelStep : IMessageHandler<CancelHotelBookingCommand>
    {
        public CancelHotelStep()
        {
        }

        public async Task Handle(CancelHotelBookingCommand message, IMessageHandlerContext context)
        {
            var hotel = message.SagaData.Hotel;
            if (hotel != null &&
                hotel.ReservationId != Guid.Empty)
            {
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Cancelling Hotel");
                    Console.ResetColor();
                }
                
                if (DateTime.UtcNow.Second >= 18 && DateTime.UtcNow.Second <= 20)
                {
                    throw new Exception($"Fake exception thrown in cancel hotel saga action.");
                }

                // reset the id
                hotel.ReservationId = Guid.Empty;
            }
            await Task.CompletedTask;
        }
    }
}
