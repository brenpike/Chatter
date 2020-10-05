using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Routing.Slips;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace TravelBooking.Application.Commands.Handlers
{
    public class BookTravelViaRoutingSlipCommandHandler : IMessageHandler<BookTravelViaRoutingSlipCommand>
    {
        private readonly IBrokeredMessageDispatcher _dispatcher;

        public BookTravelViaRoutingSlipCommandHandler(IBrokeredMessageDispatcher dispatcher) 
            => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        public Task Handle(BookTravelViaRoutingSlipCommand message, IMessageHandlerContext context)
        {
            var routingSlip = RoutingSlipBuilder.NewRoutingSlip(Guid.NewGuid())
                                                .WithRoute(RoutingStepBuilder.WithStep("book-trip-saga/1/book-rental-car")
                                                                             .WithCompensatingStep("book-trip-saga/1/cancel-rental-car"))
                                                .WithRoute(RoutingStepBuilder.WithStep("book-trip-saga/2/book-hotel")
                                                                             .WithCompensatingStep("book-trip-saga/2/cancel-hotel"))
                                                .WithRoute(RoutingStepBuilder.WithStep("book-trip-saga/3/book-flight")
                                                                             .WithCompensatingStep("book-trip-saga/3/cancel-flight"))
                                                .WithRoute(RoutingStepBuilder.WithStep("book-trip-saga/result"))
                                                .Build();

            return _dispatcher.Send(message, routingSlip);
        }
    }
}
