using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Routing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Configuration.UsingOptionsBuilders
{
    public class WhenConfiguringFluently : Testing.Core.Context
    {
        /// <summary>
        /// The POSITIVE allowlist of method names permitted to write to the <see cref="IServiceCollection"/>. Every
        /// other public instance method on every options builder is swept and required to leave the container
        /// untouched.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this is an allowlist, never a denylist. A denylist of the fluent methods that exist today would
        /// stop guarding the moment a new one is written; inverting the default means a method that is not named here
        /// is REQUIRED to leave the container empty from the day it is added.
        /// </remarks>
        private static readonly string[] _publishingMethodNames = { "Build", "Resolve", "Publish", "FromConfig" };

        [Fact]
        public void MustLeaveTheContainerEmptyForEveryFluentMethodOnMessageBrokerOptionsBuilder()
            => AssertEveryFluentMethodLeavesTheContainerEmpty(typeof(MessageBrokerOptionsBuilder), MessageBrokerOptionsBuilder.Create);

        [Fact]
        public void MustLeaveTheContainerEmptyForEveryFluentMethodOnReliabilityOptionsBuilder()
            => AssertEveryFluentMethodLeavesTheContainerEmpty(typeof(ReliabilityOptionsBuilder), ReliabilityOptionsBuilder.Create);

        [Fact]
        public void MustLeaveTheContainerEmptyForEveryFluentMethodOnRecoveryOptionsBuilder()
            => AssertEveryFluentMethodLeavesTheContainerEmpty(typeof(RecoveryOptionsBuilder), RecoveryOptionsBuilder.Create);

        [Fact]
        public void MustLeaveTheContainerEmptyForEveryFluentMethodOnCircuitBreakerOptionsBuilder()
            => AssertEveryFluentMethodLeavesTheContainerEmpty(typeof(CircuitBreakerOptionsBuilder), CircuitBreakerOptionsBuilder.Create);

        /// <summary>
        /// A second AddRecoveryOptions call must not discard the first call's predicates. Consumption is enumerable -
        /// <see cref="RetryExceptionEvaluator"/> takes every <see cref="IRetryExceptionPredicatesProvider"/> - so the
        /// contract is asserted through the evaluator a consumer actually experiences rather than through a
        /// descriptor count, which is what let the original regression through.
        /// </summary>
        [Fact]
        public void MustKeepBothRetryPredicateSetsAcrossTwoAddRecoveryOptionsCalls()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(recovery => recovery.RetryWhen<InvalidOperationException>())
                .AddRecoveryOptions(recovery => recovery.RetryWhen<FormatException>())
                .Build();

            using var provider = services.BuildServiceProvider();
            var evaluator = new RetryExceptionEvaluator(provider.GetServices<IRetryExceptionPredicatesProvider>());

            evaluator.ShouldRetry(new InvalidOperationException()).Should().BeTrue();
            evaluator.ShouldRetry(new FormatException()).Should().BeTrue();
        }

        /// <summary>
        /// A second WithCircuitBreaker call must not discard the first call's predicates.
        /// <see cref="CircuitBreakerExceptionEvaluator"/> takes every
        /// <see cref="ICircuitBreakerExceptionPredicatesProvider"/>, so both configured exception types have to stay
        /// tripping.
        /// </summary>
        [Fact]
        public void MustKeepBothCircuitBreakerPredicateSetsAcrossTwoWithCircuitBreakerCalls()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(recovery => recovery
                    .WithCircuitBreaker(circuitBreaker => circuitBreaker.IsTrippedBy<InvalidOperationException>())
                    .WithCircuitBreaker(circuitBreaker => circuitBreaker.IsTrippedBy<FormatException>()))
                .Build();

            using var provider = services.BuildServiceProvider();
            var evaluator = new CircuitBreakerExceptionEvaluator(provider.GetServices<ICircuitBreakerExceptionPredicatesProvider>());

            evaluator.ShouldTrip(new InvalidOperationException()).Should().BeTrue();
            evaluator.ShouldTrip(new FormatException()).Should().BeTrue();
        }

        [Fact]
        public void MustKeepBothReliabilitySettingsAcrossTwoAddReliabilityOptionsCalls()
        {
            var services = new ServiceCollection();

            var messageBrokerOptions = MessageBrokerOptionsBuilder.Create(services)
                .AddReliabilityOptions(reliability => reliability.WithOutboxRouting())
                .AddReliabilityOptions(reliability => reliability.WithOutboxPollingProcessor(1234))
                .Build();

            messageBrokerOptions.Reliability.RouteMessagesToOutbox.Should().BeTrue();
            messageBrokerOptions.Reliability.EnableOutboxPollingProcessor.Should().BeTrue();
            messageBrokerOptions.Reliability.OutboxProcessingIntervalInMilliseconds.Should().Be(1234);
        }

        /// <summary>
        /// The delay strategy is a single choice rather than an accumulation, so a second AddRecoveryOptions call
        /// replaces it whole - the later call's strategy resolves and carries its own argument.
        /// </summary>
        [Fact]
        public void MustResolveTheLastRequestedRetryDelayStrategyAcrossTwoAddRecoveryOptionsCalls()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(recovery => recovery.UseNoDelayRecovery())
                .AddRecoveryOptions(recovery => recovery.UseConstantDelayRecovery(250))
                .Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IRetryDelayStrategy>().Should().BeOfType<ConstantDelayRetry>();
        }

        [Fact]
        public void MustResolveNoDelayRetryWhenUseNoDelayRecoveryComposedThroughTheParentBuild()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(recovery => recovery.UseNoDelayRecovery())
                .Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IRetryDelayStrategy>().Should().BeOfType<NoDelayRetry>();
        }

        [Fact]
        public void MustResolveExponentialDelayRetryWhenUseExponentialDelayRecoveryComposedThroughTheParentBuild()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(recovery => recovery.UseExponentialDelayRecovery(4))
                .Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IRetryDelayStrategy>().Should().BeOfType<ExponentialDelayRetry>();
        }

        [Fact]
        public void MustResolveConstantDelayRetryWhenUseConstantDelayRecoveryComposedThroughTheParentBuild()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(recovery => recovery.UseConstantDelayRecovery(250))
                .Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IRetryDelayStrategy>().Should().BeOfType<ConstantDelayRetry>();
        }

        [Fact]
        public void MustResolveErrorQueueDispatcherWhenUseRouteToErrorQueueRecoveryActionComposedThroughTheParentBuild()
        {
            var services = new ServiceCollection();
            services.AddSingleton(Mock.Of<IForwardMessages>());

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(recovery => recovery.UseRouteToErrorQueueRecoveryAction())
                .Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IMaxReceivesExceededAction>().Should().BeOfType<ErrorQueueDispatcher>();
        }

        private static void AssertEveryFluentMethodLeavesTheContainerEmpty(Type builderType, Func<IServiceCollection, object> createBuilder)
        {
            var fluentMethods = SelectFluentMethods(builderType).ToList();

            fluentMethods.Should().NotBeEmpty("the sweep has to reach {0}'s fluent surface at all", builderType.Name);

            foreach (var fluentMethod in fluentMethods)
            {
                var services = new ServiceCollection();
                var invokableMethod = CloseGenericMethodOverException(fluentMethod);

                invokableMethod.Invoke(createBuilder(services), CreateArgumentsFor(invokableMethod));

                services.Should().BeEmpty("{0}.{1} is a fluent call, so it must record its intent and leave every registration to the publish path",
                                          builderType.Name,
                                          fluentMethod.Name);
            }
        }

        private static IEnumerable<MethodInfo> SelectFluentMethods(Type builderType)
            => builderType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                          .Where(method => method.GetBaseDefinition().DeclaringType != typeof(object))
                          .Where(method => !_publishingMethodNames.Contains(method.Name));

        private static MethodInfo CloseGenericMethodOverException(MethodInfo method)
        {
            if (!method.IsGenericMethodDefinition)
            {
                return method;
            }

            var typeParameters = method.GetGenericArguments();
            if (typeParameters.Length != 1)
            {
                Assert.Fail($"The fluent sweep cannot close {method.DeclaringType.Name}.{method.Name} over {typeParameters.Length} type arguments. Teach CloseGenericMethodOverException the new shape - skipping the method would drop it from the sweep silently.");
            }

            try
            {
                return method.MakeGenericMethod(typeof(InvalidOperationException));
            }
            catch (ArgumentException constraintViolation)
            {
                Assert.Fail($"The fluent sweep cannot close {method.DeclaringType.Name}.{method.Name} over InvalidOperationException: {constraintViolation.Message}. Teach CloseGenericMethodOverException the new constraint - skipping the method would drop it from the sweep silently.");
                return null;
            }
        }

        private static object[] CreateArgumentsFor(MethodInfo method)
            => method.GetParameters().Select(parameter => CreateArgumentFor(method, parameter)).ToArray();

        private static object CreateArgumentFor(MethodInfo method, ParameterInfo parameter)
        {
            var parameterType = parameter.ParameterType;

            if (parameterType.IsArray)
            {
                return Array.CreateInstance(parameterType.GetElementType(), 0);
            }

            if (parameterType.IsValueType)
            {
                return Activator.CreateInstance(parameterType);
            }

            if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(Action<>))
            {
                return CreateNoOpAction(parameterType);
            }

            Assert.Fail($"The fluent sweep cannot synthesise a '{parameterType}' argument for {method.DeclaringType.Name}.{method.Name}. Teach CreateArgumentFor the new parameter shape - skipping the method would drop it from the sweep silently and the guard would decay back into enumerating the methods someone remembered.");
            return null;
        }

        private static object CreateNoOpAction(Type actionType)
        {
            var noOpDefinition = typeof(WhenConfiguringFluently).GetMethod(nameof(IgnoreConfiguredBuilder), BindingFlags.NonPublic | BindingFlags.Static);
            return noOpDefinition.MakeGenericMethod(actionType.GetGenericArguments()[0]).CreateDelegate(actionType);
        }

        private static void IgnoreConfiguredBuilder<TBuilder>(TBuilder configuredBuilder) { }
    }
}
