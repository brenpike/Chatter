using CarRental.Application.Commands;
using CarRental.Application.DTO;
using CarRental.Application.Queries;
using Chatter.CQRS;
using Chatter.CQRS.Queries;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;

namespace CarRental.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RentalsController : Controller
    {
        private readonly IMessageDispatcher _dispatcher;
        private readonly IQueryDispatcher _queryDispatcher;

        public RentalsController(IMessageDispatcher dispatcher, IQueryDispatcher queryDispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _queryDispatcher = queryDispatcher ?? throw new ArgumentNullException(nameof(queryDispatcher));
        }

        [HttpPost]
        public async Task<IActionResult> RentCar(CarRentalDto rental)
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
            catch (Exception e)
            {
                return BadRequest(e.Message);
            }
        }

        [HttpGet]
        [Route("{carRentalId}")]
        public async Task<IActionResult> GetCarRentalById(Guid carRentalId)
        {
            var query = new GetCarRental
            {
                Id = carRentalId
            };
            var rentals = await _queryDispatcher.Query(query);
            return Ok(rentals);
        }

        //[HttpDelete]
        //[Route("{carRentalId}/cancel")]
        //public async Task<IActionResult> CancelCarRental(int carRentalId)
        //{
        //    await Task.CompletedTask;
        //    return Ok();
        //}
    }
}