using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Saga;
using System;
using tb = Samples.SharedKernel.Dtos;

namespace TravelBooking.Application.Sagas.TravelBooking.Commands
{
    [BrokeredMessage("book-trip-saga/2/cancel-hotel", nextMessage: "book-trip-saga/1/cancel-rental-car", messageDescription: "Cancel Hotel")]
    public class CancelHotelBookingCommand : ISagaMessage<tb.TravelBooking>
    {
        public tb.TravelBooking SagaData { get; set; }
        public Type SagaDataType => typeof(tb.TravelBooking);
    }
}
