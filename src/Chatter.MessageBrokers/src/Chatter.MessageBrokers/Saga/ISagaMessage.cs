using Chatter.CQRS;
using System;

namespace Chatter.MessageBrokers.Saga
{
    public interface IStartSagaMessage : ISagaMessage
    {

    }

    public interface IStartSagaMessage<out TSagaData> : IStartSagaMessage, ISagaMessage<TSagaData>
    {

    }

    public interface ICompleteSagaMessage : ISagaMessage
    {

    }

    public interface ICompleteSagaMessage<out TSagaData> : ICompleteSagaMessage, ISagaMessage<TSagaData>
    {

    }

    public interface ISagaMessage : IMessage
    {
        Type SagaDataType { get; }
    }

    public interface ISagaMessage<out TSagaData> : ISagaMessage
    {
        TSagaData SagaData { get; }
    }
}
