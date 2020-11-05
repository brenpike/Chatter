using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using System;

namespace HotelBooking.Application.Commands
{
    //[BrokeredMessage("book-trip-saga/2/book-hotel", "book-trip-saga/2/book-hotel")]
    public class BookHotelCommand : ICommand
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public DateTime CheckIn { get; set; }
        public DateTime CheckOut { get; set; }
        public Guid ReservationId { get; set; }
    }
}
