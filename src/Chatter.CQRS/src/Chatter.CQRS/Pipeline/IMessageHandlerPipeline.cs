namespace Chatter.CQRS.Pipeline
{
    public interface IMessageHandlerPipeline<in TMessage> where TMessage : IMessage
    {
    }
}
