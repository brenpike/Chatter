using CarRental.Application.Commands;
using Chatter.CQRS;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using cr = Samples.SharedKernel.Dtos;

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
        public async Task<IActionResult> RentCar(cr.CarRental rental)
        {
            var rentalCmd = new BookRentalCarCommand()
            {
                Car = rental
            };
            try
            {
                await _dispatcher.Dispatch(rentalCmd);
                return Ok();
            }
            catch (System.Exception e)
            {
                return BadRequest(e.Message);
            }
        }

        //[HttpGet]
        //[Route("{carRentalId}")]
        //public async Task<IActionResult> GetCarRentalById(int carRentalId)
        //{
        //    await Task.CompletedTask;
        //    return Ok();
        //}

        //[HttpDelete]
        //[Route("{carRentalId}/cancel")]
        //public async Task<IActionResult> CancelCarRental(int carRentalId)
        //{
        //    await Task.CompletedTask;
        //    return Ok();
        //}
    }
}