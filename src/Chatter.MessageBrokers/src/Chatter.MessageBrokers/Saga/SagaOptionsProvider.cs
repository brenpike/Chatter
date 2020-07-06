using Chatter.MessageBrokers.Saga.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Saga
{
    public class SagaOptionsProvider : ISagaOptionsProvider
    {
        private readonly ConcurrentDictionary<string, SagaOptions> _options = new ConcurrentDictionary<string, SagaOptions>();

        public SagaOptionsProvider(IEnumerable<SagaOptions> sagaOptions)
        {
            InitOptionsCache(sagaOptions);
        }

        private void InitOptionsCache(IEnumerable<SagaOptions> sagaOptions)
        {
            foreach (var options in sagaOptions)
            {
                _options[options.SagaDataType] = options;
            }
        }

        public SagaOptions GetOptionsFor<TSagaMessage>(TSagaMessage message) where TSagaMessage : ISagaMessage
        {
            return GetOptionsFor(message.SagaDataType);
        }

        public SagaOptions GetOptionsFor(Type sagaDataType)
        {
            if (_options.TryGetValue(sagaDataType.Name, out var optionsFromName))
            {
                return optionsFromName;
            }

            if (_options.TryGetValue(sagaDataType.FullName, out var optionsFromFullName))
            {
                return optionsFromFullName;
            }

            if (_options.TryGetValue(sagaDataType.AssemblyQualifiedName, out var optionsFromAssemblyQN))
            {
                return optionsFromAssemblyQN;
            }

            return new SagaOptions();
        }
    }
}
