using Microsoft.Azure.ServiceBus.Management;
using System.Collections.Generic;

namespace TravelBooking.Infrastructure
{
    public static class TopologyDefinition
    {
        public const string SagaQueuePathPrefix = "book-trip-saga";
        public const string BookRentalCarQueueName = SagaQueuePathPrefix + "/1/book-rental-car";
        public const string CancelRentalCarQueueName = SagaQueuePathPrefix + "/1/cancel-rental-car";
        public const string BookHotelQueueName = SagaQueuePathPrefix + "/2/book-hotel";
        public const string CancelHotelQueueName = SagaQueuePathPrefix + "/2/cancel-hotel";
        public const string BookFlightQueueName = SagaQueuePathPrefix + "/3/book-flight";
        public const string CancelFlightQueueName = SagaQueuePathPrefix + "/3/cancel-flight";
        public const string SagaResultQueueName = SagaQueuePathPrefix + "/result";
        public const string SagaInputQueueName = SagaQueuePathPrefix + "/input";

        //the following aren't directly related to the saga topology, they're used in workers to execute logic in distributed systems
        public const string BookFlightRequest = "book-flight-request";
        public const string BookFlightResponse = "book-flight-response";

        public static IEnumerable<QueueDescription> QueueDescriptions()
        {
            return new List<QueueDescription>
            {
                new QueueDescription(SagaResultQueueName),
                new QueueDescription(CancelFlightQueueName),
                new QueueDescription(BookFlightQueueName)
                {
                    // on failure, we move deadletter messages off to the flight 
                    // booking compensator's queue
                    EnableDeadLetteringOnMessageExpiration = true,

                    //If using DeadLetterCompensationStrategy, this should be set
                    ForwardDeadLetteredMessagesTo = CancelFlightQueueName
                },
                new QueueDescription(CancelHotelQueueName),
                new QueueDescription(BookHotelQueueName)
                {
                    // on failure, we move deadletter messages off to the hotel 
                    // booking compensator's queue
                    EnableDeadLetteringOnMessageExpiration = true,
                    //If using DeadLetterCompensationStrategy, this should be set
                    ForwardDeadLetteredMessagesTo = CancelHotelQueueName
                },
                new QueueDescription(CancelRentalCarQueueName),
                new QueueDescription(BookRentalCarQueueName)
                {
                    // on failure, we move deadletter messages off to the car rental 
                    // compensator's queue
                    EnableDeadLetteringOnMessageExpiration = true,
                    //If using DeadLetterCompensationStrategy, this should be set
                    ForwardDeadLetteredMessagesTo = CancelRentalCarQueueName
                },
                new QueueDescription(SagaInputQueueName)
                {
                    // book car is the first step
                    ForwardTo = BookRentalCarQueueName
                },
                new QueueDescription(BookFlightRequest),
                new QueueDescription(BookFlightResponse)
                {
                    RequiresSession = true
                }
            };
        }
    }
}
