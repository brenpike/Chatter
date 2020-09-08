namespace Chatter.CQRS.Pipeline
{
    public interface IMessageHandlerPipeline<in TMessage> : IMessageHandler<TMessage> where TMessage : IMessage
    {
    }
}
