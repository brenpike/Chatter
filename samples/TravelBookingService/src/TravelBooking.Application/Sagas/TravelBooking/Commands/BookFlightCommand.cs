using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Saga;
using System;
using tb = Samples.SharedKernel.Dtos;

namespace TravelBooking.Application.Sagas.TravelBooking.Commands
{
    [BrokeredMessage("book-trip-saga/3/book-flight", nextMessage: "book-trip-saga/result", compensatingMessage: "book-trip-saga/3/cancel-flight", messageDescription: "Book Flight")]
    public class BookFlightCommand : ISagaMessage<tb.TravelBooking>
    {
        public tb.TravelBooking SagaData { get; set; }
        public Type SagaDataType => typeof(tb.TravelBooking);
    }
}
