using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Exceptions;
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
        private CircuitBreakerOptionsBuilder _circuitBreakerOptionsBuilder = null;

        public const string RecoveryOptionsSectionName = "Chatter:MessageBrokers:Recovery";
        private readonly IServiceCollection _services;
        private readonly IConfigurationSection _recoveryOptionsSection;
        private int _maxRetryAttempts = _defaultMaxRetryAttempts;
        private readonly List<Predicate<Exception>> _exceptionPredicates;
        private RetryDelayStrategyKind _retryDelayStrategyKind = RetryDelayStrategyKind.Unset;
        private int _retryDelayStrategyArgument = 0;
        private bool _routeToErrorQueueOnMaxReceivesExceeded = false;

        private const int _defaultMaxRetryAttempts = 5;
        private const int _maxExponentialRetryAttempts = 15;
        private const int _minimumMaxRetryAttempts = 1;
        private const string _maxRetryAttemptsBound = "at least 1 attempt";

        // INVARIANT: the fluent surface records WHICH delay strategy was asked for as a value and never names the
        // IServiceCollection; Publish is the only place that turns the recorded kind into a registration. A single
        // field also keeps last-call-wins, matching Replace's remove-all-then-add semantics.
        private enum RetryDelayStrategyKind
        {
            Unset,
            NoDelay,
            Exponential,
            Constant
        }

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
            // INVARIANT: the ONE retained sub-builder is configured in place rather than replaced by a fresh one, so a
            // second call accumulates onto the first call's state instead of discarding it - the circuit breaker
            // exception predicates are consumed as an enumeration, so a discarded builder's predicates would never be
            // registered at all. It is also never built here: building it would register CircuitBreakerOptions before
            // this builder - and any parent above it - has bound its own section, so a consumer could resolve an
            // instance whose configured values had not been applied yet.
            var circuitBreakerOptionsBuilder = EnsureCircuitBreakerOptionsBuilder();
            builder?.Invoke(circuitBreakerOptionsBuilder);
            return this;
        }

        /// <summary>
        /// Configures the message broker infrastructure to use <see cref="NoDelayRecovery"/> as its <see cref="IRetryDelayStrategy"/>.
        /// The <see cref="IRetryDelayStrategy"/> will be triggered when message broker infrastructure fails to handle a received message.
        /// </summary>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        public RecoveryOptionsBuilder UseNoDelayRecovery()
        {
            _retryDelayStrategyKind = RetryDelayStrategyKind.NoDelay;
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
            // INVARIANT: the ceiling is applied ONCE and the clamped budget is what both the options and the delay
            // strategy are tuned to. Reading the attempt budget back at publish time instead would let a later
            // WithMaxRetryAttempts silently retune the strategy, which is exactly what the recorded argument below
            // exists to prevent.
            var clampedMaxRetryAttempts = Math.Min(maxRetryAttempts, _maxExponentialRetryAttempts);
            _maxRetryAttempts = clampedMaxRetryAttempts;
            _retryDelayStrategyKind = RetryDelayStrategyKind.Exponential;
            _retryDelayStrategyArgument = clampedMaxRetryAttempts;
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
            _retryDelayStrategyKind = RetryDelayStrategyKind.Constant;
            _retryDelayStrategyArgument = constantDelayInMilliseconds;
            return this;
        }

        /// <summary>
        /// Registers <see cref="ErrorQueueDispatcher"/> as the <see cref="IMaxReceivesExceededAction"/> that will be used after message broker infrastructure fails
        /// to handle a message after <see cref="IRetryDelayStrategy"/> has executed
        /// </summary>
        /// <returns><see cref="RecoveryOptionsBuilder"/></returns>
        public RecoveryOptionsBuilder UseRouteToErrorQueueRecoveryAction()
        {
            _routeToErrorQueueOnMaxReceivesExceeded = true;
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

        /// <summary>
        /// Produces the finalized <see cref="RecoveryOptions"/>, nested <see cref="CircuitBreakerOptions"/> included,
        /// without touching the <see cref="IServiceCollection"/>.
        /// </summary>
        /// <returns>The finalized <see cref="RecoveryOptions"/></returns>
        /// <remarks>
        /// INVARIANT: this is the COMPOSITION path and it publishes nothing. A parent builder seeds its own graph
        /// from here, binds its section over that graph and only then publishes, so no consumer can resolve an
        /// options instance the parent has not finished binding.
        /// </remarks>
        internal RecoveryOptions Resolve()
        {
            var recoveryOptions = new RecoveryOptions();
            recoveryOptions.MaxRetryAttempts = _maxRetryAttempts;
            // INVARIANT: the nested CircuitBreakerOptions is seeded BEFORE this builder's bind so the binder mutates
            // the instance the sub-builder resolved instead of replacing it. CircuitBreaker, RetryStrategy and
            // RetryWithCircuitBreakerStrategy all inject the concrete type, and Publish registers the instance
            // reachable from the finalized graph, so no consumer can reach an orphaned one.
            recoveryOptions.CircuitBreakerOptions = EnsureCircuitBreakerOptionsBuilder().Resolve();

            if (_recoveryOptionsSection != null && _recoveryOptionsSection.Exists())
            {
                // INVARIANT: bind INTO the fluent-defaulted instance and never replace it. Every property on
                // RecoveryOptions is internal set, so the binder skips all of them unless BindNonPublicProperties is
                // on; replacing the instance would additionally discard the defaults assigned above. Keys the section
                // omits therefore keep their fluent default.
                _recoveryOptionsSection.Bind(recoveryOptions, o => o.BindNonPublicProperties = true);
            }

            return recoveryOptions;
        }

        /// <summary>
        /// Refuses any value on the finalized <see cref="RecoveryOptions"/>, nested
        /// <see cref="CircuitBreakerOptions"/> included, that the runtime sink reading it cannot run with.
        /// </summary>
        /// <param name="recoveryOptions">The finalized options produced by <see cref="Resolve"/></param>
        /// <exception cref="ConfiguredValueRefusedException">A configured value the sink cannot run with</exception>
        /// <remarks>
        /// INVARIANT: validation is its own phase between <see cref="Resolve"/> and <see cref="Publish"/>. It cannot
        /// live in Resolve, because this builder has no section of its own when a parent composes it and every
        /// configured value then arrives from the parent bind AFTER Resolve has returned; and it cannot live in
        /// Publish, because a parent publishes its other children around this one and a refusal raised there would
        /// leave them registered. It recurses into the same sub-builder the composition path visits, so compose,
        /// validate and publish all walk one graph.
        /// </remarks>
        internal void Validate(RecoveryOptions recoveryOptions)
        {
            // INVARIANT: RetryStrategy starts at attempt 1 and gives up once attempts >= MaxRetryAttempts, so every
            // budget at or below 1 buys exactly one attempt. A zero or a negative therefore states a budget the sink
            // cannot express, and binding it disables retry without saying so.
            if (recoveryOptions.MaxRetryAttempts < _minimumMaxRetryAttempts)
            {
                throw new ConfiguredValueRefusedException($"{nameof(RecoveryOptions)}.{nameof(RecoveryOptions.MaxRetryAttempts)}",
                                                          recoveryOptions.MaxRetryAttempts,
                                                          _maxRetryAttemptsBound,
                                                          _recoveryOptionsSection?.Path);
            }

            // INVARIANT: a null child is left to Publish, which fails host registration loudly on it, rather than
            // dereferenced here - validation must not turn a registration failure into a NullReferenceException.
            if (recoveryOptions.CircuitBreakerOptions != null)
            {
                EnsureCircuitBreakerOptionsBuilder().Validate(recoveryOptions.CircuitBreakerOptions);
            }
        }

        /// <summary>
        /// Registers the supplied finalized <see cref="RecoveryOptions"/>, its nested
        /// <see cref="CircuitBreakerOptions"/>, this builder's configured exception predicates and the recovery
        /// services its fluent calls asked for against the <see cref="IServiceCollection"/>.
        /// </summary>
        /// <param name="recoveryOptions">The finalized options produced by <see cref="Resolve"/></param>
        /// <remarks>
        /// INVARIANT: this is the ONLY site in this builder that touches the <see cref="IServiceCollection"/>, so a
        /// fluent call alone changes nothing in the container. A parent builder calls it after its own bind so the
        /// registered instances are the finalized ones; a standalone <see cref="Build"/> calls it immediately because
        /// there is no parent left to bind. The nested options are read back off the finalized graph so the published
        /// instance is always the one every entry point reaches through
        /// <see cref="RecoveryOptions.CircuitBreakerOptions"/>.
        /// </remarks>
        internal void Publish(RecoveryOptions recoveryOptions)
        {
            EnsureCircuitBreakerOptionsBuilder().Publish(recoveryOptions.CircuitBreakerOptions);

            // INVARIANT: the recorded argument is read into a local so each delay strategy is constructed from the
            // value its own fluent call supplied, exactly as the fluent-time registration captured it.
            var retryDelayStrategyArgument = _retryDelayStrategyArgument;
            switch (_retryDelayStrategyKind)
            {
                case RetryDelayStrategyKind.NoDelay:
                    _services.Replace<IRetryDelayStrategy, NoDelayRetry>(ServiceLifetime.Scoped);
                    break;
                case RetryDelayStrategyKind.Exponential:
                    _services.Replace<IRetryDelayStrategy>(ServiceLifetime.Scoped, sp =>
                    {
                        return new ExponentialDelayRetry(retryDelayStrategyArgument);
                    });
                    break;
                case RetryDelayStrategyKind.Constant:
                    _services.Replace<IRetryDelayStrategy>(ServiceLifetime.Scoped, sp =>
                    {
                        return new ConstantDelayRetry(retryDelayStrategyArgument);
                    });
                    break;
            }

            if (_routeToErrorQueueOnMaxReceivesExceeded)
            {
                _services.Replace<IMaxReceivesExceededAction, ErrorQueueDispatcher>(ServiceLifetime.Scoped);
            }

            if (_exceptionPredicates.Count > 0)
            {
                _services.AddSingleton<IRetryExceptionPredicatesProvider>(new ConfigRetryExceptionPredicatesProvider(_exceptionPredicates));
            }

            // INVARIANT: every single-instance resolution of RecoveryOptions returns the instance published here -
            // AddBuiltOptions registers it as the concrete type and as IOptions, IOptionsSnapshot and
            // IOptionsMonitor over that same instance. The container's options factory is deliberately NOT used:
            // a Configure<RecoveryOptions>(section) registration would build a second instance that never saw
            // the fluent defaults, so its CircuitBreakerOptions would be null and a section that omits
            // MaxRetryAttempts would resolve it as 0, turning the first failure straight into
            // MaxRetryAttemptsExceededException. The concrete registration is APPENDED rather than replaced, so
            // a second Build() on the same IServiceCollection takes over single-instance resolution and leaves
            // the earlier instances reachable through IEnumerable<RecoveryOptions> - each seeded by its own
            // Resolve(), so no enumeration can surface an unseeded object.
            _services.AddBuiltOptions(recoveryOptions);
        }

        public RecoveryOptions Build()
        {
            var recoveryOptions = Resolve();
            Validate(recoveryOptions);
            Publish(recoveryOptions);
            return recoveryOptions;
        }

        // INVARIANT: Resolve and Publish must reach the SAME sub-builder, so the default one is created once and
        // retained here rather than constructed at each call site.
        private CircuitBreakerOptionsBuilder EnsureCircuitBreakerOptionsBuilder()
        {
            if (_circuitBreakerOptionsBuilder == null)
            {
                _circuitBreakerOptionsBuilder = CircuitBreakerOptionsBuilder.Create(_services);
            }

            return _circuitBreakerOptionsBuilder;
        }
    }
}
