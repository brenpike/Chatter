using Azure.Core;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Options.UsingServiceBusOptionsBuilder
{
    // Uses a real ServiceCollection + ConfigurationBuilder (no Moq of sealed config types).
    // Each fluent setter must return the same builder instance for chaining.
    public class WhenConfiguringFluently : Testing.Core.Context
    {
        private readonly IServiceCollection _services = new ServiceCollection();
        private readonly IConfiguration _configuration =
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        private ServiceBusOptionsBuilder CreateSut()
            => ServiceBusOptionsBuilder.Create(_services, _configuration);

        [Fact]
        public void MustReturnSameBuilderFromWithConnectionString()
        {
            var sut = CreateSut();
            sut.WithConnectionString("conn").Should().BeSameAs(sut);
        }

        [Fact]
        public void MustReturnSameBuilderFromWithMaxConcurrentCalls()
        {
            var sut = CreateSut();
            sut.WithMaxConcurrentCalls(8).Should().BeSameAs(sut);
        }

        [Fact]
        public void MustReturnSameBuilderFromWithPrefetchCount()
        {
            var sut = CreateSut();
            sut.WithPrefetchCount(3).Should().BeSameAs(sut);
        }

        [Fact]
        public void MustReturnSameBuilderFromWithNoRetry()
        {
            var sut = CreateSut();
            sut.WithNoRetry().Should().BeSameAs(sut);
        }

        [Fact]
        public void MustReturnSameBuilderFromWithExponentialDelay()
        {
            var sut = CreateSut();
            sut.WithExponentialDelay(5, 30, 1, 3).Should().BeSameAs(sut);
        }

        [Theory]
        [InlineData(101)]
        [InlineData(500)]
        [InlineData(-1)]
        public void MustThrowNamingMaximumRetryCountWhenFluentRetryCountOutsideTheSdkRange(int maximumRetryCount)
        {
            // The fluent path shares ONE guarded construction site with the configuration path, so a retry
            // count the SDK cannot run with is reported the same way here instead of raising a bare
            // ArgumentOutOfRangeException from the MaxRetries setter. There is deliberately no "greater than
            // zero means configured" fall-through on this path: a caller who passes -1 stated a value.
            var sut = CreateSut();

            Action withExponentialDelay = () => sut.WithExponentialDelay(maximumRetryCount, 30, 1, 3);

            withExponentialDelay.Should().Throw<ServiceBusRetryOptionsValidationException>()
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MaximumRetryCount)}*")
                .WithMessage($"*{maximumRetryCount}*");
        }

        [Fact]
        public void MustThrowNamingMinimumBackoffInSecondsWhenFluentBackoffIsNegative()
        {
            // TimeSpan.FromSeconds accepts a negative, so the builder itself never gated this: the rejection
            // came from the SDK's own Delay setter as a bare ArgumentOutOfRangeException naming 'Delay', a
            // member no operator supplied. The shared guard names the knob that was passed instead.
            var sut = CreateSut();

            Action withExponentialDelay = () => sut.WithExponentialDelay(5, 30, -1, 3);

            withExponentialDelay.Should().Throw<ServiceBusRetryOptionsValidationException>()
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MinimumBackoffInSeconds)}*");
        }

        [Fact]
        public void MustNotTreatFluentMinimumBackoffGreaterThanMaximumBackoffAsAViolation()
        {
            // The SDK CLAMPS a Delay above MaxDelay while computing each retry delay. That is not a crash, so
            // the shared guard must not turn it into a violation on this path either.
            var sut = CreateSut();

            Action withExponentialDelay = () => sut.WithExponentialDelay(5, 5, 60, 3);

            withExponentialDelay.Should().NotThrow();
        }

        [Fact]
        public void MustReturnSameBuilderFromAddTokenProviderInstance()
        {
            var sut = CreateSut();
            sut.AddTokenProvider(new MarkerTokenCredential()).Should().BeSameAs(sut);
        }

        [Fact]
        public void MustReturnSameBuilderFromAddTokenProviderFactory()
        {
            var sut = CreateSut();
            sut.AddTokenProvider(() => new MarkerTokenCredential()).Should().BeSameAs(sut);
        }

        [Fact]
        public void MustReturnSameBuilderFromUseConfig()
        {
            var sut = CreateSut();
            sut.UseConfig().Should().BeSameAs(sut);
        }

        [Fact]
        public void MustExposeServiceCollection()
            => CreateSut().Services.Should().BeSameAs(_services);

        private sealed class MarkerTokenCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new AccessToken("t", DateTimeOffset.MaxValue);

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(new AccessToken("t", DateTimeOffset.MaxValue));
        }
    }
}
