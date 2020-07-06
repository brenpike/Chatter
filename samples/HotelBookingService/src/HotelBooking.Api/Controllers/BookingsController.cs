using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace HotelBooking.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BookingsController : Controller
    {
        public BookingsController()
        {
        }

        [HttpPost]
        public async Task<IActionResult> BookHotel(int request)
        {
            await Task.CompletedTask;
            return Ok();
        }

        [HttpGet]
        [Route("{hotelBookingId}")]
        public async Task<IActionResult> GetHotelBookingById(int hotelBookingId)
        {
            await Task.CompletedTask;
            return Ok();
        }

        [HttpDelete]
        [Route("{hotelBookingId}/cancel")]
        public async Task<IActionResult> CancelHotelBooking(int hotelBookingId)
        {
            await Task.CompletedTask;
            return Ok();
        }
    }
}