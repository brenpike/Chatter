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
                                                .WithRoute("book-trip-saga/1/book-rental-car")
                                                .WithRoute("book-trip-saga/2/book-hotel")
                                                .WithRoute("book-trip-saga/3/book-flight")
                                                .WithRoute("book-trip-saga/result")
                                                .Build();

            return _dispatcher.Send(message, routingSlip);
        }
    }
}
