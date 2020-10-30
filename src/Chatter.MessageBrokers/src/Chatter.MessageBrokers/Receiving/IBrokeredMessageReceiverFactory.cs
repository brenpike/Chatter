﻿using Chatter.CQRS;

namespace Chatter.MessageBrokers.Receiving
{
    public interface IBrokeredMessageReceiverFactory
    {
        IBrokeredMessageReceiver<TMessage> Create<TMessage>(string receivingEntityPath, string errorQueuePath = null, string description = null) where TMessage : class, IMessage;
    }
}
