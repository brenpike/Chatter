using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.MessageBrokers
{
    public class MessagingInfrastructureProvider : IMessagingInfrastructureProvider
    {
        private readonly ConcurrentDictionary<string, IMessagingInfrastructure> _infrastructures = new ConcurrentDictionary<string, IMessagingInfrastructure>();
        private readonly ILogger<MessagingInfrastructureProvider> _logger;

        public MessagingInfrastructureProvider(IEnumerable<IMessagingInfrastructure> infrastructures, ILogger<MessagingInfrastructureProvider> logger)
        {
            _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
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

        public IMessagingInfrastructure Get(string infrastructureType)
        {
            if (string.IsNullOrWhiteSpace(infrastructureType))
            {
                var inf = _infrastructures.First().Value;
                _logger.LogDebug($"No {nameof(infrastructureType)} was provided to {nameof(Get)}. Returning first registered infrastructure of type {inf.Type}.");
                return inf;
            }

            if (!(_infrastructures.TryGetValue(infrastructureType, out var infrastructure)))
            {
                throw new KeyNotFoundException($"No {nameof(IMessagingInfrastructure)} was found for type '{infrastructureType}'.");
            }

            return infrastructure;
        }

        public IMessagingInfrastructureReceiver GetReceiver(string type)
            => Get(type).ReceiveInfrastructure;

        public IMessagingInfrastructureDispatcher GetDispatcher(string type)
            => Get(type).DispatchInfrastructure;
    }
}
