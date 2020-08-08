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
    public class MessageDispatcherFactory : IMessageDispatcherFactory
    {
        private readonly ConcurrentDictionary<Type, IMessageDispatcher> _dispatchers = new ConcurrentDictionary<Type, IMessageDispatcher>();
        readonly object _sync = new object();
        private readonly IEnumerable<IMessageDispatcherProvider> _providers;

        public MessageDispatcherFactory(IEnumerable<IMessageDispatcherProvider> providers)
        {
            _providers = providers;
        }

        ///<inheritdoc/>
        public IMessageDispatcher CreateDispatcher<TMessage>() where TMessage : IMessage
        {
            lock (_sync)
            {
                var providerType = GetProviderTypeFromMessage(typeof(TMessage));

                if (!_dispatchers.TryGetValue(providerType, out var dispatcher))
                {
                    var provider = _providers.Where(p =>
                                        p.DispatchType == providerType ||
                                        providerType.IsGenericType && p.DispatchType == providerType.GetGenericTypeDefinition()
                                   ).LastOrDefault();

                    if (provider == default)
                    {
                        throw new KeyNotFoundException($"No {typeof(IMessageDispatcherProvider).Name} exists for type '{providerType.Name}'.");
                    }

                    dispatcher = provider.GetDispatcher();
                    _dispatchers[providerType] = dispatcher;
                }

                return dispatcher;
            }
        }

        private Type GetProviderTypeFromMessage(Type messageType)
        {
            var interfaces = messageType.GetTypeInfo().ImplementedInterfaces;
            var firstInterfaceThatIsNotIMessage = interfaces.Where(i => i != typeof(IMessage)).LastOrDefault();

            if (firstInterfaceThatIsNotIMessage is null)
            {
                return messageType;
            }

            if (firstInterfaceThatIsNotIMessage.IsGenericType)
            {
                return GetProviderTypeFromMessage(firstInterfaceThatIsNotIMessage.GetGenericTypeDefinition());
            }
            else
            {
                return firstInterfaceThatIsNotIMessage;
            }
        }
    }
}
