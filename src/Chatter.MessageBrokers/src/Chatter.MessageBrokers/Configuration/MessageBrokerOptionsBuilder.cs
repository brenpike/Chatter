using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Saga.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Configuration
{
    public class MessageBrokerOptionsBuilder
    {
        private readonly IServiceCollection _services;
        private readonly IConfiguration _configuration;
        private MessageBrokerOptions _messageBrokerOptions;
        private ReliabilityOptions _reliabilityOptions;
        private List<SagaOptions> _sagaOptions;
        private Func<IServiceCollection> _optionConfigurator;
        private const string _messageBrokerSectionName = "Chatter:MessageBrokers";
        private const string _reliabilityOptionsSectionName = _messageBrokerSectionName + ":Reliability";
        private const string _sagaOptionsSectionName = _messageBrokerSectionName + ":Sagas";

        internal MessageBrokerOptionsBuilder(IServiceCollection services, IConfiguration configuration)
        {
            _services = services;
            _configuration = configuration;
            _messageBrokerOptions = new MessageBrokerOptions();
            _optionConfigurator = () => _services.Configure<MessageBrokerOptions>(configuration.GetSection(_messageBrokerSectionName));
        }

        public MessageBrokerOptionsBuilder AddMessageBrokerOptions(Action<MessageBrokerOptions> builder)
        {
            _optionConfigurator = () => _services.Configure(builder);
            return this;
        }

        public MessageBrokerOptionsBuilder AddMessageBrokerOptions(string messageBrokerSectionName = _messageBrokerSectionName)
        {
            _optionConfigurator = () => _services.Configure<MessageBrokerOptions>(_configuration.GetSection(messageBrokerSectionName));
            return this;
        }

        public MessageBrokerOptionsBuilder AddReliabilityOptions(string reliabilityOptionsSectionName = _reliabilityOptionsSectionName)
        {
            var reliabilityOptions = _configuration.GetSection(reliabilityOptionsSectionName).Get<ReliabilityOptions>();

            if (reliabilityOptions is null)
            {
                throw new ArgumentNullException(nameof(reliabilityOptions), $"No reliability options found in section '{reliabilityOptionsSectionName}'");
            }

            _reliabilityOptions = reliabilityOptions;

            return this;
        }

        public MessageBrokerOptionsBuilder AddSagaOptions(IConfiguration configuration, string sagaOptionsSectionName = _sagaOptionsSectionName)
        {
            var sagaOptions = configuration.GetSection(sagaOptionsSectionName).Get<List<SagaOptions>>();

            if (sagaOptions is null)
            {
                throw new ArgumentNullException(nameof(sagaOptions), $"No saga options found in section '{sagaOptionsSectionName}'");
            }

            foreach (var option in sagaOptions)
            {
                if (string.IsNullOrWhiteSpace(option.SagaDataType))
                {
                    throw new ArgumentNullException(nameof(option.SagaDataType), "A saga data type is required to register saga specific options.");
                }
            }

            _sagaOptions = sagaOptions;

            return this;
        }

        private void PostConfiguration(MessageBrokerOptions messageBrokerOptions)
        {
            if (_sagaOptions != null)
            {
                messageBrokerOptions.Sagas = _sagaOptions;
            }

            if (_reliabilityOptions != null)
            {
                messageBrokerOptions.Reliability = _reliabilityOptions;
            }

            _messageBrokerOptions = messageBrokerOptions;
        }

        internal MessageBrokerOptions Build()
        {
            _services.AddOptions<MessageBrokerOptions>()
                     .ValidateDataAnnotations()
                     .PostConfigure(options =>
                     {
                         PostConfiguration(options);
                     });

            _optionConfigurator?.Invoke();

            _services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<MessageBrokerOptions>>().Value);
            _services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<MessageBrokerOptions>>().Value?.Reliability ?? new ReliabilityOptions());
            _services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<MessageBrokerOptions>>().Value?.Sagas ?? new List<SagaOptions>());

            return _messageBrokerOptions;
        }
    }
}
