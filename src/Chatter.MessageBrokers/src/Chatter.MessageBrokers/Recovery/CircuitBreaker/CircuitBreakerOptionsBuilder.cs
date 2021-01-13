using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using Chatter.CQRS;

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

        /// <summary>
        /// Sets the time to wait in seconds before the circuit breaker can enter the half-open state from the open state. Default is 15 seconds.
        /// </summary>
        /// <param name="timeInSeconds">The time to wait in seconds</param>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
        public CircuitBreakerOptionsBuilder SetOpenToHalfOpenWaitTime(int timeInSeconds)
        {
            _openToHalfOpenWaitTimeInSeconds = timeInSeconds;
            return this;
        }

        /// <summary>
        /// Sets the number of consumers allowed to enter the half-open state. Default is 1.
        /// </summary>
        /// <param name="numberOfAttempts">The number of concurrent consumers that can enter the half-open state</param>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
        public CircuitBreakerOptionsBuilder SetConcurrentHalfOpenAttempts(int numberOfAttempts)
        {
            _concurrentHalfOpenAttempts = numberOfAttempts;
            return this;
        }

        /// <summary>
        /// Sets the number of failures allowed before the circuit breaker enters the open state. Default is 5.
        /// </summary>
        /// <param name="numberOfFailures">The number of consecutive failures allowed before circuit breaker enters the open state</param>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
        public CircuitBreakerOptionsBuilder SetNumberOfFailuresBeforeOpen(int numberOfFailures)
        {
            _numberOfFailuresBeforeOpen = numberOfFailures;
            return this;
        }

        /// <summary>
        /// Sets the number of successes are required while the circuit breaker is in the half-open state before the circuit can be closed. This 
        /// ensures that services recovering from a recent failure are not overwhelmed. Default is 3.
        /// </summary>
        /// <param name="numberOfSuccessfulAttempts">The number of sucesses required while half-open to close the circuit</param>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
        public CircuitBreakerOptionsBuilder SetNumberOfHalfOpenSuccessesBeforeClose(int numberOfSuccessfulAttempts)
        {
            _numberOfHalfOpenSuccessesToClose = numberOfSuccessfulAttempts;
            return this;
        }

        /// <summary>
        /// Sets the time the circuit can remain open before a <see cref="CriticalFailureEvent"/> is raised by the circuit breaker. Critical failure logic 
        /// should be consumer specific and defined in <see cref="IMessageHandler{CriticalFailureEvent}"/>. Default is 1800 (30 minutes).
        /// </summary>
        /// <param name="timeInSeconds">The time in seconds before a <see cref="CriticalFailureEvent"/> is dispatched</param>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
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
