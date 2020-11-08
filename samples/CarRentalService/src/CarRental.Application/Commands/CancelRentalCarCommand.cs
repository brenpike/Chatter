using CarRental.Application.DTO;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;

namespace CarRental.Application.Commands
{
    [BrokeredMessage("book-trip-saga/1/cancel-rental-car", "book-trip-saga/1/cancel-rental-car")]
    public class CancelRentalCarCommand : ICommand
    {
        public CarRentalDto Car { get; set; }
    }
}
