using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.MessageBrokers
{
    public class MessagingInfrastructureProvider : IMessagingInfrastructureProvider
    {
        private readonly ConcurrentDictionary<string, IMessagingInfrastructure> _infrastructures = new ConcurrentDictionary<string, IMessagingInfrastructure>();
        private readonly ILogger<MessagingInfrastructureProvider> _logger;
        private readonly IMessagingInfrastructure _default;

        public MessagingInfrastructureProvider(IEnumerable<IMessagingInfrastructure> infrastructures, ILogger<MessagingInfrastructureProvider> logger)
        {
            _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
            _default = infrastructures.FirstOrDefault();
            _logger.LogInformation($"Setting default {nameof(IMessagingInfrastructure)} to '{_default?.Type}'.");
            InitProviderLookup(infrastructures);
        }

        private void InitProviderLookup(IEnumerable<IMessagingInfrastructure> infrastructures)
        {
            foreach (var infrastructure in infrastructures)
            {
                _infrastructures[infrastructure.Type] = infrastructure;
                _logger.LogTrace($"Added infrastructure of type '{infrastructure.Type}' to provider");
            }
        }

        public IMessagingInfrastructure GetInfrastructure(string infrastructureType)
        {
            if (_infrastructures.Count == 0)
            {
                throw new InvalidOperationException("No messaging infrastructure was found. Add messaging infrastructure when configuring your application.");
            }

            if (string.IsNullOrWhiteSpace(infrastructureType))
            {
                _logger.LogTrace($"No '{nameof(infrastructureType)}' was provided to {nameof(GetInfrastructure)}. Using default infrastructure ({_default.Type}).");
                return _default;
            }

            if (!(_infrastructures.TryGetValue(infrastructureType, out var infrastructure)))
            {
                throw new KeyNotFoundException($"No {nameof(IMessagingInfrastructure)} was found for type '{infrastructureType}'.");
            }

            _logger.LogTrace($"Found infrastructure for type '{infrastructureType}'.");
            return infrastructure;
        }

        public IMessagingInfrastructureReceiver GetReceiver(string type)
            => GetInfrastructure(type).ReceiveInfrastructure;

        public IMessagingInfrastructureDispatcher GetDispatcher(string type)
            => GetInfrastructure(type).DispatchInfrastructure;
    }
}
