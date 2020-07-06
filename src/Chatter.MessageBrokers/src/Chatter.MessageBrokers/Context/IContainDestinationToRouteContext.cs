using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    public interface IContainDestinationToRouteContext : IContainContext
    {
        string DestinationPath { get; }
        OutboundBrokeredMessage CreateDestinationMessage(InboundBrokeredMessage inboundBrokeredMessage);
    }
}
