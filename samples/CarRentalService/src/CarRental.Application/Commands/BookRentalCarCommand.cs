using CarRental.Application.DTO;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;

namespace CarRental.Application.Commands
{
    [BrokeredMessage("book-trip-saga/1/book-rental-car", "book-trip-saga/1/book-rental-car")]
    public class BookRentalCarCommand : ICommand
    {
        public CarRentalDto Car { get; set; }
    }
}
