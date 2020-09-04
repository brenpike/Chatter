using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    public class ServiceBusOptionsBuilder
    {
        private readonly IServiceCollection _services;

        internal ServiceBusOptionsBuilder(IServiceCollection services)
        {
            _services = services;
        }

        private void ConfigureOptionsBuilder(Func<IServiceCollection> configure)
        {
            _services.AddOptions<ServiceBusOptions>()
                     .ValidateDataAnnotations() //TODO: requires Microsoft.Extensions.Options.DataAnnotations. Remove and validate manually?
                     .PostConfigure(options =>
                     {
                         PostConfiguration(options);
                     });

            configure();

            _services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<ServiceBusOptions>>().Value);
        }

        public void AddServiceBusOptions(Action<ServiceBusOptions> builder)
        {
            ConfigureOptionsBuilder(() => _services.Configure(builder));
        }

        public void AddServiceBusOptions(IConfiguration configuration, string configSectionName)
        {
            ConfigureOptionsBuilder(() => _services.Configure<ServiceBusOptions>(configuration.GetSection(configSectionName)));
        }

        private void PostConfiguration(ServiceBusOptions serviceBusConfig)
        {
            if (!Enum.IsDefined(typeof(TransportType), serviceBusConfig.TransportType))
            {
                throw new ArgumentOutOfRangeException(nameof(serviceBusConfig.TransportType), $"Value specified was not a valid {typeof(TransportType).FullName}.");
            }

            if (!Enum.IsDefined(typeof(ReceiveMode), serviceBusConfig.ReceiveMode))
            {
                throw new ArgumentOutOfRangeException(nameof(serviceBusConfig.ReceiveMode), $"Value specified was not a valid {typeof(ReceiveMode).FullName}.");
            }

            if (serviceBusConfig.RetryPolicy == null)
            {
                serviceBusConfig.Policy = RetryPolicy.Default;
            }
            else if (serviceBusConfig.RetryPolicy.MaximumRetryCount == 0 && serviceBusConfig.RetryPolicy.MaximumBackoff == 0 && serviceBusConfig.RetryPolicy.MinimumBackoff == 0)
            {
                serviceBusConfig.Policy = RetryPolicy.NoRetry;
            }
            else
            {
                var retryExponential = new RetryExponential(TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.MinimumBackoff),
                                            TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.MaximumBackoff),
                                            serviceBusConfig.RetryPolicy.MaximumRetryCount);
                serviceBusConfig.Policy = retryExponential;
            }
        }
    }
}
