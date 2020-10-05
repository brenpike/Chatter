using Chatter.CQRS;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Commands;
using TravelBooking.Application.DTO;

namespace TravelBooking.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BookingsController : Controller
    {
        private readonly IMessageDispatcher _dispatcher;

        public BookingsController(IMessageDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        [HttpPut("topology/create")]
        public async Task CreateTopology()
        {
            await _dispatcher.Dispatch(new CreateTravelBookingTopologyCommand());
        }

        [HttpPut("topology/delete")]
        public async Task DeleteTopology()
        {
            await _dispatcher.Dispatch(new DeleteTravelBookingTopologyCommand());
        }

        [HttpPut("orchestration")]
        public async Task BookTravelViaSagaOrchestration([FromBody] TravelBookingDto travelBooking)
        {
            var tbc = new BookTravelViaOrchestrationCommand()
            {
                SagaData = travelBooking
            };
            await _dispatcher.Dispatch(tbc);
        }

        [HttpPut("routingslip")]
        public async Task BookTravelViaRoutingSlip([FromBody] TravelBookingDto travelBooking)
        {
            var tbc = new BookTravelViaRoutingSlipCommand()
            {
                Id = travelBooking.Id,
                Car = travelBooking.Car,
                Hotel = travelBooking.Hotel,
                Flight = travelBooking.Flight
            };
            await _dispatcher.Dispatch(tbc);
        }
    }
}