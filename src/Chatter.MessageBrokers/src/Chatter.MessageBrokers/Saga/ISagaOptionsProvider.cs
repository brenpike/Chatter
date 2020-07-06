using Chatter.MessageBrokers.Saga.Configuration;
using System;

namespace Chatter.MessageBrokers.Saga
{
    public interface ISagaOptionsProvider
    {
        SagaOptions GetOptionsFor<TSagaMessage>(TSagaMessage message) where TSagaMessage : ISagaMessage;
        SagaOptions GetOptionsFor(Type sagaDataType);
    }
}
