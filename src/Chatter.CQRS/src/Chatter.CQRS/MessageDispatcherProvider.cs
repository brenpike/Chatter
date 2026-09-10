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
        // INVARIANT: dispatchers resolved by walking implemented interfaces are memoized here and are never written
        // back to _dispatchers, which the interface walk itself reads. A resolved message type added to _dispatchers
        // would become a key a later message type could match, making dispatcher selection depend on resolution
        // history and on the unspecified order of TypeInfo.ImplementedInterfaces.
        // INVARIANT: this cache is per provider instance, never static. Both this provider and the IDispatchMessages
        // it holds are registered per scope, so a process-wide cache would dispatch through a disposed scope.
        private readonly ConcurrentDictionary<Type, IDispatchMessages> _resolvedDispatchers = new ConcurrentDictionary<Type, IDispatchMessages>();

        public MessageDispatcherProvider(IEnumerable<IDispatchMessages> providers)
        {
            _ = providers ?? throw new ArgumentNullException(nameof(providers), $"Enumerable of {nameof(IDispatchMessages)} cannot be null");

            foreach (var prov in providers)
            {
                if (prov.DispatchType is null)
                {
                    throw new ArgumentNullException(nameof(prov.DispatchType), $"A non-null dispatch type is required");
                }

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

            if (_resolvedDispatchers.TryGetValue(typeof(TMessage), out var resolved))
            {
                return resolved;
            }

            if (TryGetDispatcherByImplementedInterface(typeof(TMessage), out var dispatcher))
            {
                _resolvedDispatchers[typeof(TMessage)] = dispatcher;
                return dispatcher;
            }

            throw new KeyNotFoundException($"No {typeof(IDispatchMessages).Name} exists for type '{typeof(TMessage).Name}'.");
        }

        internal virtual bool TryGetDispatcherByImplementedInterface(Type messageType, out IDispatchMessages dispatcher)
        {
            var interfaces = messageType.GetTypeInfo().ImplementedInterfaces;

            foreach (var i in interfaces)
            {
                if (_dispatchers.TryGetValue(i, out dispatcher))
                {
                    return true;
                }
            }

            dispatcher = null;
            return false;
        }
    }
}
