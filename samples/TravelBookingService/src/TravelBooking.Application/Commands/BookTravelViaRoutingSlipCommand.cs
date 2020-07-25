using Chatter.CQRS.Commands;
using tb = Samples.SharedKernel.Dtos;

namespace TravelBooking.Application.Commands
{
    public class BookTravelViaRoutingSlipCommand : ICommand
    {
        public tb.TravelBooking Booking { get; set; }
    }
}
