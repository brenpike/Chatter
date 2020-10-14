using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    public class ServiceBusOptionsBuilder
    {
        private readonly IServiceCollection _services;

        private ServiceBusOptions _serviceBusOptions;
        private Func<IServiceCollection> _optionConfigurator;
        private ITokenProvider _tokenProvider;

        internal ServiceBusOptionsBuilder(IServiceCollection services)
        {
            _services = services;
            _serviceBusOptions = new ServiceBusOptions();
            _tokenProvider = new NullTokenProvider();
        }

        public void AddServiceBusOptions(Action<ServiceBusOptions> builder)
        {
            _optionConfigurator = () => _services.Configure(builder);
        }

        public ServiceBusOptionsBuilder AddServiceBusOptions(IConfiguration configuration, string configSectionName = "Chatter:AzureServiceBus")
        {
            _optionConfigurator = () => _services.Configure<ServiceBusOptions>(configuration.GetSection(configSectionName));
            return this;
        }

        public ServiceBusOptionsBuilder AddTokenProvider(Func<ITokenProvider> tokenProviderFactory)
        {
            _tokenProvider = tokenProviderFactory?.Invoke() ?? new NullTokenProvider();
            return this;
        }

        private void PostConfiguration(ServiceBusOptions serviceBusConfig)
        {
            if (serviceBusConfig.RetryPolicy == null)
            {
                serviceBusConfig.Policy = RetryPolicy.Default;
            }
            else if (serviceBusConfig.RetryPolicy.MaximumRetryCount == 0
                && serviceBusConfig.RetryPolicy.MaximumBackoffInSeconds == 0
                && serviceBusConfig.RetryPolicy.MinimumBackoffInSeconds == 0
                && serviceBusConfig.RetryPolicy.DeltaBackoffInSeconds == 0)
            {
                serviceBusConfig.Policy = RetryPolicy.NoRetry;
            }
            else
            {
                var retryExponential = new RetryExponential(TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.MinimumBackoffInSeconds),
                                            TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.MaximumBackoffInSeconds),
                                            TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.DeltaBackoffInSeconds),
                                            serviceBusConfig.RetryPolicy.MaximumRetryCount);
                serviceBusConfig.Policy = retryExponential;
            }

            serviceBusConfig.TokenProvider = _tokenProvider;

            _serviceBusOptions = serviceBusConfig;
        }

        internal ServiceBusOptions Build()
        {
            _services.AddOptions<ServiceBusOptions>()
                     .ValidateDataAnnotations()
                     .PostConfigure(options =>
                     {
                         PostConfiguration(options);
                     });

            _optionConfigurator?.Invoke();

            _services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<ServiceBusOptions>>().Value);

            return _serviceBusOptions;
        }
    }
}
