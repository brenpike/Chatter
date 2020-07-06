using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Saga;
using System;
using tb = Samples.SharedKernel.Dtos;

namespace TravelBooking.Application.Sagas.TravelBooking.Commands
{
    [BrokeredMessage("book-trip-saga/1/book-rental-car", nextMessage: "book-trip-saga/2/book-hotel", compensatingMessage: "book-trip-saga/1/cancel-rental-car", messageDescription: "Book Car Rental")]
    public class BookCarRentalCommand : ISagaMessage<tb.TravelBooking>
    {
        public tb.TravelBooking SagaData { get; set; }
        public Type SagaDataType => typeof(tb.TravelBooking);
    }
}
