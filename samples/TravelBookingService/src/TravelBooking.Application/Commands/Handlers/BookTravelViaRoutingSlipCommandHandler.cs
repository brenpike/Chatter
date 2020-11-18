using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Routing.Slips;
using System;
using System.Threading.Tasks;

namespace TravelBooking.Application.Commands.Handlers
{
    public class BookTravelViaRoutingSlipCommandHandler : IMessageHandler<BookTravelViaRoutingSlipCommand>
    {
        public Task Handle(BookTravelViaRoutingSlipCommand message, IMessageHandlerContext context)
        {
            var routingSlip = RoutingSlipBuilder.NewRoutingSlip(Guid.NewGuid())
                                                .WithRoute("book-trip-saga/1/book-rental-car")
                                                .WithRoute("book-trip-saga/2/book-hotel")
                                                .WithRoute("book-trip-saga/3/book-flight")
                                                .WithRoute("book-trip-saga/result")
                                                .Build();

            return context.Send(message, routingSlip);
        }
    }
}
