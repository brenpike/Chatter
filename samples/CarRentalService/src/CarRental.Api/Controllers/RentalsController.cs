using Chatter.CQRS;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace CarRental.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RentalsController : Controller
    {
        private readonly IMessageDispatcher _dispatcher;

        public RentalsController(IMessageDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new System.ArgumentNullException(nameof(dispatcher));
        }

        [HttpPost]
        public async Task<IActionResult> RentCar(int request)
        {
            await Task.CompletedTask;
            return Ok();
        }

        [HttpGet]
        [Route("{carRentalId}")]
        public async Task<IActionResult> GetCarRentalById(int carRentalId)
        {
            await Task.CompletedTask;
            return Ok();
        }

        [HttpDelete]
        [Route("{carRentalId}/cancel")]
        public async Task<IActionResult> CancelCarRental(int carRentalId)
        {
            await Task.CompletedTask;
            return Ok();
        }
    }
}