using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Saga;
using System;
using tb = Samples.SharedKernel.Dtos;

namespace TravelBooking.Application.Sagas.TravelBooking.Commands
{
    [BrokeredMessage("book-trip-saga/2/book-hotel", nextMessage: "book-trip-saga/3/book-flight", compensatingMessage: "book-trip-saga/2/cancel-hotel", messageDescription: "Book Hotel")]
    public class BookHotelCommand : ISagaMessage<tb.TravelBooking>
    {
        public tb.TravelBooking SagaData { get; set; }
        public Type SagaDataType => typeof(tb.TravelBooking);
    }
}
