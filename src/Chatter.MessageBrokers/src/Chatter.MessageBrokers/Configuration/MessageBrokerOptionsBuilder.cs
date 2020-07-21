using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Saga.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Configuration
{
    public class MessageBrokerOptionsBuilder
    {
        private readonly IServiceCollection _services;
        private ReliabilityOptions _reliabilityOptions;
        private List<SagaOptions> _sagaOptions;

        internal MessageBrokerOptionsBuilder(IServiceCollection services)
        {
            _services = services;
            _reliabilityOptions = new ReliabilityOptions();
            _sagaOptions = new List<SagaOptions>();
        }

        public MessageBrokerOptionsBuilder AddReliabilityOptions(IConfiguration configuration, string reliabilityOptionsSectionName = "Chatter:MessageBrokers:Reliability")
        {
            var reliabilityOptions = configuration.GetSection(reliabilityOptionsSectionName).Get<ReliabilityOptions>();

            if (reliabilityOptions is null)
            {
                throw new ArgumentNullException(nameof(reliabilityOptions), $"No reliability options found in section '{reliabilityOptionsSectionName}'");
            }

            _services.AddSingleton(reliabilityOptions);

            _reliabilityOptions = reliabilityOptions;

            return this;
        }

        public MessageBrokerOptionsBuilder AddSagaOptions(IConfiguration configuration, string sagaOptionsSectionName = "Chatter:MessageBrokers:Sagas")
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

                if (!Enum.TryParse<TransactionMode>(option.DefaultTransactionMode, out var transactionMode))
                {
                    option.TransactionMode = TransactionMode.None;
                }
                else
                {
                    option.TransactionMode = transactionMode;
                }

                _services.AddSingleton(option);
            }

            _sagaOptions = sagaOptions;

            return this;
        }

        internal MessageBrokerOptions Build()
        {
            var options = new MessageBrokerOptions()
            {
                Reliability = _reliabilityOptions,
                Sagas = _sagaOptions
            };

            _services.AddSingleton(options);

            return options;
        }
    }
}
