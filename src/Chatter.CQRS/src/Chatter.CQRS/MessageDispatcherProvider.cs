using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            if (_dispatchers.TryGetValue(typeof(TMessage), out var self))
            {
                return self;
            }

            var interfaces = typeof(TMessage).GetTypeInfo().ImplementedInterfaces;

            foreach (var i in interfaces)
            {
                if (_dispatchers.TryGetValue(i, out var dispatcher))
                {
                    return dispatcher;
                }
            }

            throw new KeyNotFoundException($"No {typeof(IDispatchMessages).Name} exists for type '{typeof(TMessage).Name}'.");
        }
    }
}
