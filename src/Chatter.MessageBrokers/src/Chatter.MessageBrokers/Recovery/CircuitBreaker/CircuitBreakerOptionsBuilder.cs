using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Exceptions;
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

        private const int _minimumConcurrentHalfOpenAttempts = 1;
        private const int _millisecondsInASecond = 1000;
        // INVARIANT: both circuit breaker time knobs are scheduled through the BCL's maximum supported timeout -
        // one by Task.Delay, the other by Timer.Change - so one ceiling bounds both. The builder tests derive it
        // from each of those two sinks independently rather than restating it, so a change to either bound is loud.
        private const long _maximumSchedulableMilliseconds = 4_294_967_294;
        private const string _concurrentHalfOpenAttemptsBound = "at least 1 attempt";
        private const string _openToHalfOpenWaitTimeBound = "a number of seconds Task.Delay can wait";
        private const string _secondsOpenBeforeCriticalFailureNotificationBound = "a number of seconds Timer.Change can schedule";

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

        /// <summary>
        /// Produces the finalized <see cref="CircuitBreakerOptions"/> without touching the
        /// <see cref="IServiceCollection"/>.
        /// </summary>
        /// <returns>The finalized <see cref="CircuitBreakerOptions"/></returns>
        /// <remarks>
        /// INVARIANT: this is the COMPOSITION path and it publishes nothing. A parent builder seeds its own graph
        /// from here, binds its section over that graph and only then publishes, so no consumer can resolve an
        /// options instance the parent has not finished binding.
        /// </remarks>
        internal CircuitBreakerOptions Resolve()
        {
            var circuitBreakerOptions = new CircuitBreakerOptions();
            circuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds = _openToHalfOpenWaitTimeInSeconds;
            circuitBreakerOptions.ConcurrentHalfOpenAttempts = _concurrentHalfOpenAttempts;
            circuitBreakerOptions.NumberOfFailuresBeforeOpen = _numberOfFailuresBeforeOpen;
            circuitBreakerOptions.NumberOfHalfOpenSuccessesToClose = _numberOfHalfOpenSuccessesToClose;
            circuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification = _secondsOpenBeforeCriticalFailureNotification;

            if (_circuitBreakerOptionsSection != null && _circuitBreakerOptionsSection.Exists())
            {
                // INVARIANT: bind INTO the fluent-defaulted instance and never replace it. Every property on
                // CircuitBreakerOptions is internal set, so the binder skips all of them unless
                // BindNonPublicProperties is on; replacing the instance would additionally discard the defaults
                // assigned above. Keys the section omits therefore keep their fluent default.
                _circuitBreakerOptionsSection.Bind(circuitBreakerOptions, o => o.BindNonPublicProperties = true);
            }

            return circuitBreakerOptions;
        }

        /// <summary>
        /// Refuses any value on the finalized <see cref="CircuitBreakerOptions"/>.
        /// </summary>
        /// <param name="circuitBreakerOptions">The finalized options produced by <see cref="Resolve"/></param>
        /// <exception cref="ConfiguredValueRefusedException">A configured value was refused</exception>
        /// <remarks>
        /// INVARIANT: validation is its own phase between <see cref="Resolve"/> and <see cref="Publish"/>. It cannot
        /// live in Resolve, because this builder has no section of its own when a parent composes it and every
        /// configured value then arrives from the parent bind AFTER Resolve has returned; and it cannot live in
        /// Publish, because a parent publishes its other children around this one and a refusal raised there would
        /// leave them registered.
        /// <br/>
        /// NumberOfFailuresBeforeOpen and NumberOfHalfOpenSuccessesToClose are deliberately NOT validated: the state
        /// store counts them as <c>++count &gt;= threshold</c>, so no value faults the sink and a non-positive
        /// threshold means 'trip, or close, on the first', which is intent an operator is entitled to state.
        /// </remarks>
        internal void Validate(CircuitBreakerOptions circuitBreakerOptions)
        {
            // INVARIANT: the circuit breaker turns this count into new SemaphoreSlim(n, n) and awaits it before every
            // half-open trial. A negative throws out of the breaker's own constructor; a zero is a perfectly legal
            // semaphore that admits nothing, so the host starts and the first trial then blocks for good. Both modes
            // are the same defect - a count below one buys no trial - so one bound covers them.
            if (circuitBreakerOptions.ConcurrentHalfOpenAttempts < _minimumConcurrentHalfOpenAttempts)
            {
                throw new ConfiguredValueRefusedException($"{nameof(CircuitBreakerOptions)}.{nameof(CircuitBreakerOptions.ConcurrentHalfOpenAttempts)}",
                                                          circuitBreakerOptions.ConcurrentHalfOpenAttempts,
                                                          _concurrentHalfOpenAttemptsBound,
                                                          _circuitBreakerOptionsSection?.Path);
            }

            // INVARIANT: zero stays legal and is load-bearing - a zero wait is always already elapsed, so an open
            // circuit never refuses and the first call after opening goes straight to a trial. The state store's
            // cooling gate accepts every TimeSpan, so Task.Delay is the strictly binding sink for this value.
            if (!CanSchedule(circuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds))
            {
                throw new ConfiguredValueRefusedException($"{nameof(CircuitBreakerOptions)}.{nameof(CircuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds)}",
                                                          circuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds,
                                                          _openToHalfOpenWaitTimeBound,
                                                          _circuitBreakerOptionsSection?.Path);
            }

            // INVARIANT: the circuit breaker arms its critical-failure notification by handing this value, as a
            // TimeSpan, to Timer.Change.
            if (!CanSchedule(circuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification))
            {
                throw new ConfiguredValueRefusedException($"{nameof(CircuitBreakerOptions)}.{nameof(CircuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification)}",
                                                          circuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification,
                                                          _secondsOpenBeforeCriticalFailureNotificationBound,
                                                          _circuitBreakerOptionsSection?.Path);
            }
        }

        private static bool CanSchedule(int timeInSeconds)
            => timeInSeconds >= 0 && (long)timeInSeconds * _millisecondsInASecond <= _maximumSchedulableMilliseconds;

        /// <summary>
        /// Registers the supplied finalized <see cref="CircuitBreakerOptions"/> and this builder's configured
        /// exception predicates against the <see cref="IServiceCollection"/>.
        /// </summary>
        /// <param name="circuitBreakerOptions">The finalized options produced by <see cref="Resolve"/></param>
        /// <remarks>
        /// INVARIANT: this is the ONLY site in this builder that touches the <see cref="IServiceCollection"/> with
        /// built options. A parent builder calls it after its own bind so the registered instance is the finalized
        /// one; a standalone <see cref="Build"/> calls it immediately because there is no parent left to bind.
        /// </remarks>
        internal void Publish(CircuitBreakerOptions circuitBreakerOptions)
        {
            if (_exceptionPredicates.Count > 0)
            {
                _services.AddSingleton<ICircuitBreakerExceptionPredicatesProvider>(new ConfigCircuitBreakerExceptionPredicatesProvider(_exceptionPredicates));
            }

            // INVARIANT: every single-instance resolution of CircuitBreakerOptions returns the instance published
            // here - AddBuiltOptions registers it as the concrete type and as IOptions, IOptionsSnapshot
            // and IOptionsMonitor over that same instance. The container's options factory is deliberately
            // NOT used: a Configure<CircuitBreakerOptions>(section) registration would build a second
            // instance that never saw the fluent defaults, so a section that omits
            // ConcurrentHalfOpenAttempts would resolve it as 0 and CircuitBreaker would be constructed
            // from that 0 rather than from the resolved default. The concrete registration is
            // APPENDED rather than replaced, so a second Build() on the same IServiceCollection takes
            // over single-instance resolution and leaves the earlier instances reachable through
            // IEnumerable<CircuitBreakerOptions> - each seeded by its own Resolve(), so no enumeration can
            // surface an unseeded object.
            _services.AddBuiltOptions(circuitBreakerOptions);
        }

        public CircuitBreakerOptions Build()
        {
            var circuitBreakerOptions = Resolve();
            Validate(circuitBreakerOptions);
            Publish(circuitBreakerOptions);
            return circuitBreakerOptions;
        }
    }
}
