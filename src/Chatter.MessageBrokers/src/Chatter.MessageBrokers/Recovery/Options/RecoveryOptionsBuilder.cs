using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery.Options
{
    public class RecoveryOptionsBuilder
    {
        private CircuitBreakerOptions _circuitBreakerOptions = null;

        public const string RecoveryOptionsSectionName = "Chatter:MessageBrokers:Recovery";
        private readonly IServiceCollection _services;
        private readonly IConfigurationSection _recoveryOptionsSection;
        private int _maxRetryAttempts = _defaultMaxRetryAttempts;
        private readonly List<Predicate<Exception>> _exceptionPredicates;

        private const int _defaultMaxRetryAttempts = 5;
        private const int _maxExponentialRetryAttempts = 15;

        private RecoveryOptionsBuilder(IServiceCollection services) : this(services, null) { }
        private RecoveryOptionsBuilder(IServiceCollection services, IConfigurationSection section)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _recoveryOptionsSection = section;
            _exceptionPredicates = new List<Predicate<Exception>>();
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
            var builder = new RecoveryOptionsBuilder(services, section);
            return builder.Build();
        }

        /// <summary>
        /// Allows <see cref="CircuitBreaker"/> configuration
        /// </summary>
        /// <param name="builder">The <see cref="CircuitBreakerOptionsBuilder"/></param>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        public RecoveryOptionsBuilder WithCircuitBreaker(Action<CircuitBreakerOptionsBuilder> builder)
        {
            var b = CircuitBreakerOptionsBuilder.Create(_services);
            builder?.Invoke(b);
            _circuitBreakerOptions = b.Build();
            return this;
        }

        /// <summary>
        /// Configures the message broker infrastructure to use <see cref="NoDelayRecovery"/> as its <see cref="IRetryDelayStrategy"/>.
        /// The <see cref="IRetryDelayStrategy"/> will be triggered when message broker infrastructure fails to handle a received message.
        /// </summary>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        public RecoveryOptionsBuilder UseNoDelayRecovery()
        {
            _services.Replace<IRetryDelayStrategy, NoDelayRetry>(ServiceLifetime.Scoped);
            return this;
        }

        /// <summary>
        /// Sets the maximum number of retries that will be attempted when message broker infrastructure fails to handle a received message.
        /// The default is 5.
        /// </summary>
        /// <param name="maxRetryAttempts">The maximum number of retry attempts</param>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        public RecoveryOptionsBuilder WithMaxRetryAttempts(int maxRetryAttempts)
        {
            _maxRetryAttempts = maxRetryAttempts;
            return this;
        }

        /// <summary>
        /// Configures message broker infrastructure to use <see cref="ExponentialDelayRetry"/> (exponential backoff) as its <see cref="IRetryDelayStrategy"/>. 
        /// The <see cref="IRetryDelayStrategy"/> will be triggered when message broker infrastructure fails to handle a received message.
        /// </summary>
        /// <param name="maxRetryAttempts">The maximum number of exponentially backed-off retry attemps</param>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        /// <remarks>
        /// Exponential delay per attempt:
        ///<br>Attempt #1  - 0s</br>
        ///<br>Attempt #2  - 2s</br>
        ///<br>Attempt #3  - 4s</br>
        ///<br>Attempt #4  - 8s</br>
        ///<br>Attempt #5  - 16s</br>
        ///<br>Attempt #6  - 32s</br>
        ///<br>Attempt #7  - 1m 4s</br>
        ///<br>Attempt #8  - 2m 8s</br>
        ///<br>Attempt #9  - 4m 16s</br>
        ///<br>Attempt #10 - 8m 32s</br>
        ///<br>Attempt #11 - 17m 4s</br>
        ///<br>Attempt #12 - 34m 8s</br>
        ///<br>Attempt #13 - 1h 8m 16s</br>
        ///<br>Attempt #14 - 2h 16m 32s</br>
        ///<br>Attempt #15 - 4h 33m 4s</br>
        /// </remarks>
        public RecoveryOptionsBuilder UseExponentialDelayRecovery(int maxRetryAttempts)
        {
            _maxRetryAttempts = Math.Min(maxRetryAttempts, _maxExponentialRetryAttempts);
            _services.Replace<IRetryDelayStrategy>(ServiceLifetime.Scoped, sp =>
            {
                return new ExponentialDelayRetry(maxRetryAttempts);
            });
            return this;
        }

        /// <summary>
        /// Configures message broker infrastructure to use <see cref="ConstantDelayRetry"/> as its <see cref="IRetryDelayStrategy"/>. 
        /// The <see cref="IRetryDelayStrategy"/> will be triggered when message broker infrastructure fails to handle a received message.
        /// </summary>
        /// <param name="constantDelayInMilliseconds">The constant time in milliseconds to wait before the next retry attempt</param>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        public RecoveryOptionsBuilder UseConstantDelayRecovery(int constantDelayInMilliseconds)
        {
            _services.Replace<IRetryDelayStrategy>(ServiceLifetime.Scoped, sp =>
            {
                return new ConstantDelayRetry(constantDelayInMilliseconds);
            });
            return this;
        }

        /// <summary>
        /// Registers <see cref="ErrorQueueDispatcher"/> as the <see cref="IMaxReceivesExceededAction"/> that will be used after message broker infrastructure fails
        /// to handle a message after <see cref="IRetryDelayStrategy"/> has executed
        /// </summary>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        public RecoveryOptionsBuilder UseRouteToErrorQueueRecoveryAction()
        {
            _services.Replace<IMaxReceivesExceededAction, ErrorQueueDispatcher>(ServiceLifetime.Scoped);
            return this;
        }


        /// <summary>
        /// Allows configuration of exception predicates that will cause an operation to be retried
        /// </summary>
        /// <param name="exceptions">One or more exception predicates</param>
        /// <returns><see cref="CircuitBreakerOptionsBuilder"/></returns>
        /// <example>builder.RetryWhen(e => e is CustomExceptionType c && c.IsTransient);</example>
        public RecoveryOptionsBuilder RetryWhen(params Predicate<Exception>[] exceptions)
        {
            if (exceptions != null)
            {
                _exceptionPredicates.AddRange(exceptions);
            }
            return this;
        }

        /// <summary>
        /// Sets the type of exception that will cause an operation to be retried
        /// </summary>
        /// <typeparam name="TException">The type of exception to trigger a retry operation</typeparam>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        public RecoveryOptionsBuilder RetryWhen<TException>() where TException : Exception
            => RetryWhen(e => e is TException);

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
                recoveryOptions.MaxRetryAttempts = _defaultMaxRetryAttempts;
                recoveryOptions.CircuitBreakerOptions = _circuitBreakerOptions;
            }

            if (_exceptionPredicates.Count > 0)
            {
                _services.AddSingleton<IRetryExceptionPredicatesProvider>(new ConfigRetryExceptionPredicatesProvider(_exceptionPredicates));
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
