using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

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
        private readonly IConfigurationSection _circuitBreakerOptionsSection;
        private readonly List<Predicate<Exception>> _exceptionPredicates;

        public static CircuitBreakerOptionsBuilder Create(IServiceCollection services)
            => new CircuitBreakerOptionsBuilder(services);

        private CircuitBreakerOptionsBuilder(IServiceCollection services) : this(services, null) { }
        private CircuitBreakerOptionsBuilder(IServiceCollection services, IConfigurationSection circuitBreakerOptionsSection)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _circuitBreakerOptionsSection = circuitBreakerOptionsSection;
            _exceptionPredicates = new List<Predicate<Exception>>();
        }

        public static CircuitBreakerOptions FromConfig(IServiceCollection services, IConfiguration configuration, string circuitBreakerOptionsSectionName = CircuitBreakerOptionsSectionName)
        {
            var section = configuration?.GetSection(circuitBreakerOptionsSectionName);
            var builder = new CircuitBreakerOptionsBuilder(services, section);
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
        /// Sets the time the circuit can remain open before a critical event is logged. Default is 1800 (30 minutes).
        /// </summary>
        /// <param name="timeInSeconds">The time in seconds</param>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
        public CircuitBreakerOptionsBuilder SetTimeOpenBeforeCriticalEvent(int timeInSeconds)
        {
            _secondsOpenBeforeCriticalFailureNotification = timeInSeconds;
            return this;
        }

        /// <summary>
        /// Allows configuration of exception predicates that will cause the circuit breaker to be tripped
        /// </summary>
        /// <param name="exceptions">One or more exception predicates</param>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
        /// <example>builder.IsTrippedBy(e => e is CustomExceptionType c && c.IsTransient);</example>
        public CircuitBreakerOptionsBuilder IsTrippedBy(params Predicate<Exception>[] exceptions)
        {
            if (exceptions != null)
            {
                _exceptionPredicates.AddRange(exceptions);
            }
            return this;
        }

        /// <summary>
        /// Sets the type of exception that will cause the circuit breaker to be tripped
        /// </summary>
        /// <typeparam name="TException">The type of exception to trigger the circuit breaker</typeparam>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
        public CircuitBreakerOptionsBuilder IsTrippedBy<TException>() where TException : Exception
            => IsTrippedBy(e => e is TException);

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

            if (_exceptionPredicates.Count > 0)
            {
                _services.AddSingleton<ICircuitBreakerExceptionPredicatesProvider>(new ConfigCircuitBreakerExceptionPredicatesProvider(_exceptionPredicates));
            }

            _services.AddSingleton(circuitBreakerOptions);

            return circuitBreakerOptions;
        }
    }
}
