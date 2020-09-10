using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS
{
    /// <summary>
    /// Creates an <see cref="IMessageDispatcher"/>
    /// </summary>
    public class MessageDispatcherProvider : IMessageDispatcherProvider
    {
        private readonly ConcurrentDictionary<Type, IDispatchMessages> _dispatchers = new ConcurrentDictionary<Type, IDispatchMessages>();

        public MessageDispatcherProvider(IEnumerable<IDispatchMessages> providers)
        {
            foreach (var prov in providers)
            {
                _dispatchers[prov.DispatchType] = prov;
            }
        }

        ///<inheritdoc/>
        public IDispatchMessages GetDispatcher<TMessage>() where TMessage : IMessage
        {
            var dispatcherType = GetDispatcherTypeFromMessage(typeof(TMessage));

            if (!_dispatchers.TryGetValue(dispatcherType, out var dispatcher))
            {
                throw new KeyNotFoundException($"No {typeof(IDispatchMessages).Name} exists for type '{dispatcherType.Name}'.");
            }

            return dispatcher;
        }

        private Type GetDispatcherTypeFromMessage(Type messageType)
        {
            var interfaces = messageType.GetTypeInfo().ImplementedInterfaces;
            var firstInterfaceThatIsNotIMessage = interfaces.Where(i => i != typeof(IMessage)).LastOrDefault();

            if (firstInterfaceThatIsNotIMessage is null)
            {
                return messageType;
            }

            if (firstInterfaceThatIsNotIMessage.IsGenericType)
            {
                return GetDispatcherTypeFromMessage(firstInterfaceThatIsNotIMessage.GetGenericTypeDefinition());
            }
            else
            {
                return firstInterfaceThatIsNotIMessage;
            }
        }
    }
}
