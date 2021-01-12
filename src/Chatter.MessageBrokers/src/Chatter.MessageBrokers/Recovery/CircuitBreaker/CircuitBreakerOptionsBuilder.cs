using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public class CircuitBreakerOptionsBuilder
    {
        private int _openToHalfOpenWaitTimeInSeconds = 15;
        private int _concurrentHalfOpenAttempts = 1;
        private int _numberOfFailuresBeforeOpen = 5;
        private int _numberOfHalfOpenSuccessesToClose = 3;
        private int _secondsOpenBeforeCriticalFailureNotification = 1800;

        public const string CircuitBreakerOptionsSectionName = "Chatter:MessageBrokers:Recovery:CircuitBreaker";
        private readonly IServiceCollection _services;
        private readonly IConfiguration _configuration;
        private readonly IConfigurationSection _circuitBreakerOptionsSection;

        public static CircuitBreakerOptionsBuilder Create(IServiceCollection services)
            => new CircuitBreakerOptionsBuilder(services);

        private CircuitBreakerOptionsBuilder(IServiceCollection services) : this(services, null, null) { }
        private CircuitBreakerOptionsBuilder(IServiceCollection services, IConfiguration configuration, IConfigurationSection circuitBreakerOptionsSection)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _configuration = configuration;
            _circuitBreakerOptionsSection = circuitBreakerOptionsSection;
        }

        public static CircuitBreakerOptions FromConfig(IServiceCollection services, IConfiguration configuration, string circuitBreakerOptionsSectionName = CircuitBreakerOptionsSectionName)
        {
            var section = configuration?.GetSection(circuitBreakerOptionsSectionName);
            var builder = new CircuitBreakerOptionsBuilder(services, configuration, section);
            return builder.Build();
        }

        public CircuitBreakerOptionsBuilder SetOpenToHalfOpenWaitTime(int timeInSeconds)
        {
            _openToHalfOpenWaitTimeInSeconds = timeInSeconds;
            return this;
        }

        public CircuitBreakerOptionsBuilder SetConcurrentHalfOpenAttempts(int numberOfAttempts)
        {
            _concurrentHalfOpenAttempts = numberOfAttempts;
            return this;
        }

        public CircuitBreakerOptionsBuilder SetNumberOfFailuresBeforeOpen(int numberOfFailures)
        {
            _numberOfFailuresBeforeOpen = numberOfFailures;
            return this;
        }

        public CircuitBreakerOptionsBuilder SetNumberOfHalfOpenSuccessesBeforeClose(int numberOfSuccessfulAttempts)
        {
            _numberOfHalfOpenSuccessesToClose = numberOfSuccessfulAttempts;
            return this;
        }

        public CircuitBreakerOptionsBuilder SetTimeOpenBeforeRaisingCriticalFailureEvent(int timeInSeconds)
        {
            _secondsOpenBeforeCriticalFailureNotification = timeInSeconds;
            return this;
        }

        public CircuitBreakerOptions Build()
        {
            var circuitBreakerOptions = new CircuitBreakerOptions();
            if (_circuitBreakerOptionsSection != null && _circuitBreakerOptionsSection.Exists())
            {
                circuitBreakerOptions = _circuitBreakerOptionsSection.Get<CircuitBreakerOptions>();
                _services.Configure<CircuitBreakerOptions>(_circuitBreakerOptionsSection);
            }
            else
            {
                circuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds = _openToHalfOpenWaitTimeInSeconds;
                circuitBreakerOptions.ConcurrentHalfOpenAttempts = _concurrentHalfOpenAttempts;
                circuitBreakerOptions.NumberOfFailuresBeforeOpen = _numberOfFailuresBeforeOpen;
                circuitBreakerOptions.NumberOfHalfOpenSuccessesToClose = _numberOfHalfOpenSuccessesToClose;
                circuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification = _secondsOpenBeforeCriticalFailureNotification;
            }

            _services.AddSingleton(circuitBreakerOptions);

            return circuitBreakerOptions;
        }
    }
}
