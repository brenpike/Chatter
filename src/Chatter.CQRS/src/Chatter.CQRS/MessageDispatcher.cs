using Chatter.CQRS.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.CQRS
{
    ///<inheritdoc/>
    internal class MessageDispatcher : IMessageDispatcher
    {
        private readonly IMessageDispatcherProvider _dispatcherProvider;

        public MessageDispatcher(IMessageDispatcherProvider dispatcherProvider) 
            => _dispatcherProvider = dispatcherProvider ?? throw new ArgumentNullException(nameof(dispatcherProvider));

        ///<inheritdoc/>
        public Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage 
            => Dispatch(message, new MessageHandlerContext());

        ///<inheritdoc/>
        public Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            var dispatcher = _dispatcherProvider.GetDispatcher<TMessage>();
            return dispatcher.Dispatch(message, messageHandlerContext);
        }
    }
}
