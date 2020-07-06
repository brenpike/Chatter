using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Saga;
using System;
using tb = Samples.SharedKernel.Dtos;

namespace TravelBooking.Application.Sagas.TravelBooking.Commands
{
    [BrokeredMessage("book-trip-saga/result", nextMessage: null, compensatingMessage: null, messageDescription: "Complete Booking")]
    public class TravelBookingResultCommand : ICompleteSagaMessage<tb.TravelBooking>
    {
        public tb.TravelBooking SagaData { get; set; }
        public Type SagaDataType => typeof(tb.TravelBooking);
    }
}
