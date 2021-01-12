using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.Recovery.Options
{
    public class RecoveryOptionsBuilder
    {
        private CircuitBreakerOptions _circuitBreakerOptions = null;

        public const string RecoveryOptionsSectionName = "Chatter:MessageBrokers:Recovery";
        private readonly IConfiguration _configuration;
        private readonly IServiceCollection _services;
        private readonly IConfigurationSection _recoveryOptionsSection;

        private int _maxRetryAttempts = 3;

        private RecoveryOptionsBuilder(IServiceCollection services) : this(services, null, null) { }
        private RecoveryOptionsBuilder(IServiceCollection services, IConfiguration configuration, IConfigurationSection section)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _configuration = configuration;
            _recoveryOptionsSection = section;
        }

        /// <summary>
        /// Creates a new <see cref="RecoveryOptionsBuilder"/>
        /// </summary>
        /// <param name="services">A <see cref="IServiceCollection"/> used by the builder</param>
        /// <returns>A new <see cref="RecoveryOptionsBuilder"/></returns>
        public static RecoveryOptionsBuilder Create(IServiceCollection services)
            => new RecoveryOptionsBuilder(services);

        /// <summary>
        /// Creates <see cref="RecoveryOptions"/> from configuration
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> </param>
        /// <param name="configuration"></param>
        /// <param name="recoveryOptionsSectionName"></param>
        /// <returns></returns>
        public static RecoveryOptions FromConfig(IServiceCollection services, IConfiguration configuration, string recoveryOptionsSectionName = RecoveryOptionsSectionName)
        {
            var section = configuration?.GetSection(recoveryOptionsSectionName);
            var builder = new RecoveryOptionsBuilder(services, configuration, section);
            return builder.Build();
        }

        public RecoveryOptionsBuilder WithCircuitBreaker(Action<CircuitBreakerOptionsBuilder> builder)
        {
            var b = CircuitBreakerOptionsBuilder.Create(_services);
            builder?.Invoke(b);
            _circuitBreakerOptions = b.Build();
            return this;
        }

        public RecoveryOptionsBuilder UseNoDelayRecovery()
        {
            _services.Replace<IDelayedRecovery, NoDelayRecovery>(ServiceLifetime.Scoped);
            return this;
        }

        public RecoveryOptionsBuilder WithMaxRetryAttempts(int maxRetryAttempts)
        {
            _maxRetryAttempts = maxRetryAttempts;
            return this;
        }

        public RecoveryOptionsBuilder UseExponentialDelayRecovery(int maxRetryAttempts)
        {
            _maxRetryAttempts = maxRetryAttempts;
            _services.Replace<IDelayedRecovery>(ServiceLifetime.Scoped, sp =>
            {
                return new ExponentialDelayRecovery(maxRetryAttempts);
            });
            return this;
        }

        public RecoveryOptionsBuilder UseConstantDelayRecovery(int constantDelayInMilliseconds)
        {
            _services.Replace<IDelayedRecovery>(ServiceLifetime.Scoped, sp =>
            {
                return new ConstantDelayRecovery(constantDelayInMilliseconds);
            });
            return this;
        }

        public RecoveryOptionsBuilder UseRouteToErrorQueueRecoveryAction()
        {
            _services.Replace<IRecoveryAction, ErrorQueueDispatcher>(ServiceLifetime.Scoped);
            return this;
        }

        public RecoveryOptions Build()
        {
            var recoveryOptions = new RecoveryOptions();
            if (_recoveryOptionsSection != null && _recoveryOptionsSection.Exists())
            {
                recoveryOptions = _recoveryOptionsSection.Get<RecoveryOptions>();
                _services.Configure<RecoveryOptions>(_recoveryOptionsSection);
            }
            else
            {
                recoveryOptions.MaxRetryAttempts = _maxRetryAttempts;
                recoveryOptions.CircuitBreakerOptions = _circuitBreakerOptions;
            }

            if (recoveryOptions.CircuitBreakerOptions is null)
            {
                recoveryOptions.CircuitBreakerOptions = CircuitBreakerOptionsBuilder.Create(_services).Build();
            }

            _services.AddSingleton(recoveryOptions);

            return recoveryOptions;
        }
    }
}
