using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Saga.Configuration
{
    public class SagaOptionsBuilder
    {
        private readonly IServiceCollection _services;

        internal SagaOptionsBuilder(IServiceCollection services)
        {
            _services = services;
        }

        public void AddAllSagaOptions(IConfiguration configuration, string sagaOptionsSectionName = "Chatter:Sagas")
        {
            var sagaOptions = configuration.GetSection(sagaOptionsSectionName).Get<List<SagaOptions>>();
            foreach (var option in sagaOptions)
            {
                if (string.IsNullOrWhiteSpace(option.SagaDataType))
                {
                    throw new ArgumentNullException(nameof(option.SagaDataType), "A saga data type is required to register saga specific options.");
                }
                _services.AddSingleton(option);
            }
        }

        public void AddSagaOptions(IConfiguration configuration, string specificSagaOptionsSectionName)
        {
            var sagaOptions = configuration.GetSection(specificSagaOptionsSectionName).Get<SagaOptions>();
            _services.AddSingleton(sagaOptions);
        }
    }
}
