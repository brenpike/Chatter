using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Saga;
using System;
using TravelBooking.Application.DTO;

namespace TravelBooking.Application.Commands
{
    [BrokeredMessage("book-trip-saga/1/book-rental-car")]
    public class BookTravelViaOrchestrationCommand : IStartSagaMessage<TravelBookingDto>
    {
        public TravelBookingDto SagaData { get; set; }
        public Type SagaDataType => typeof(TravelBookingDto);
    }
}
