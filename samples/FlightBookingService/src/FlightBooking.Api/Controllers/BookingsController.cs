using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace FlightBooking.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BookingsController : Controller
    {
        public BookingsController()
        {
        }

        [HttpPost]
        public async Task<IActionResult> BookFlight(int request)
        {
            await Task.CompletedTask;
            return Ok();
        }

        [HttpGet]
        [Route("{flightBookingId}")]
        public async Task<IActionResult> GetFlightBookingById(int flightBookingId)
        {
            await Task.CompletedTask;
            return Ok();
        }

        [HttpDelete]
        [Route("{flightBookingId}/cancel")]
        public async Task<IActionResult> CancelFlightBooking(int flightBookingId)
        {
            await Task.CompletedTask;
            return Ok();
        }
    }
}