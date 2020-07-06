using Chatter.CQRS;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using TravelBooking.Application.Commands;
using tb = Samples.SharedKernel.Dtos;

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

        [HttpPost]
        public async Task<IActionResult> BookTravel(int request)
        {
            await Task.CompletedTask;
            return Ok();
        }

        [HttpGet]
        [Route("{travelBookingId}")]
        public async Task<IActionResult> GetTravelBookingById(int travelBookingId)
        {
            await Task.CompletedTask;
            return Ok();
        }

        [HttpDelete]
        [Route("{travelBookingId}/cancel")]
        public async Task<IActionResult> CancelTravelBooking(int travelBookingId)
        {
            await Task.CompletedTask;
            return Ok();
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

        [HttpPut("orchestration/book")]
        public async Task BookTravelViaSagaOrchestration([FromBody] tb.TravelBooking travelBooking)
        {
            var tbc = new BookTravelCommand()
            {
                SagaData = travelBooking
            };
            await _dispatcher.Dispatch(tbc);
        }

        [HttpPut("choreography/book")]
        public async Task BookTravelViaSagaChoreography([FromBody] tb.TravelBooking travelBooking)
        {
            await Task.CompletedTask;
        }
    }
}