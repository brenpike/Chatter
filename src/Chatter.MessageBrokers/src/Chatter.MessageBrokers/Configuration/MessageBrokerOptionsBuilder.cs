using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Reliability.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.Configuration
{
    public class MessageBrokerOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private readonly IConfiguration _configuration;
        private TransactionMode _transactionMode = TransactionMode.ReceiveOnly;
        private ReliabilityOptions _reliabilityOptions = null;
        private RecoveryOptions _recoveryOptions = null;
        private readonly IConfigurationSection _messageBrokerOptionsSection = null;

        public const string MessageBrokerSectionName = "Chatter:MessageBrokers";

        public static MessageBrokerOptionsBuilder Create(IServiceCollection services)
            => new MessageBrokerOptionsBuilder(services);

        private MessageBrokerOptionsBuilder(IServiceCollection services) : this(services, null, null) { }
        internal MessageBrokerOptionsBuilder(IServiceCollection services, IConfiguration configuration, IConfigurationSection section = null)
        {
            Services = services;
            _configuration = configuration;
            _messageBrokerOptionsSection = section;
        }

        public MessageBrokerOptionsBuilder WithTransactionMode(TransactionMode transactionMode)
        {
            _transactionMode = transactionMode;
            return this;
        }

        public MessageBrokerOptions FromConfig(string messageBrokerSectionName = MessageBrokerSectionName)
            => FromConfig(Services, _configuration, messageBrokerSectionName);

        public static MessageBrokerOptions FromConfig(IServiceCollection services, IConfiguration configuration, string messageBrokerSectionName = MessageBrokerSectionName)
        {
            var section = configuration?.GetSection(messageBrokerSectionName);
            var builder = new MessageBrokerOptionsBuilder(services, configuration, section);
            return builder.Build();
        }

        public MessageBrokerOptionsBuilder AddReliabilityOptions(Action<ReliabilityOptionsBuilder> builder)
        {
            var b = ReliabilityOptionsBuilder.Create(Services);
            builder?.Invoke(b);
            _reliabilityOptions = b.Build();
            return this;
        }

        public MessageBrokerOptionsBuilder AddRecoveryOptions(Action<RecoveryOptionsBuilder> builder)
        {
            var b = RecoveryOptionsBuilder.Create(Services);
            builder?.Invoke(b);
            _recoveryOptions = b.Build();
            return this;
        }

        internal MessageBrokerOptions Build()
        {
            var messageBrokerOptions = new MessageBrokerOptions();
            if (_messageBrokerOptionsSection != null && _messageBrokerOptionsSection.Exists())
            {
                messageBrokerOptions = _messageBrokerOptionsSection.Get<MessageBrokerOptions>();
                Services.Configure<MessageBrokerOptions>(_messageBrokerOptionsSection);
            }
            else
            {
                messageBrokerOptions.Reliability = _reliabilityOptions;
                messageBrokerOptions.Recovery = _recoveryOptions;
                messageBrokerOptions.TransactionMode = _transactionMode;
            }

            if (messageBrokerOptions.Reliability is null)
            {
                messageBrokerOptions.Reliability = ReliabilityOptionsBuilder.Create(Services).Build();
            }

            if (messageBrokerOptions.Recovery is null)
            {
                messageBrokerOptions.Recovery = RecoveryOptionsBuilder.Create(Services).Build();
            }

            Services.AddSingleton(messageBrokerOptions);

            return messageBrokerOptions;
        }
    }
}
