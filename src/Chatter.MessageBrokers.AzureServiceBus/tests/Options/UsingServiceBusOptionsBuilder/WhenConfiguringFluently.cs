using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
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

        [Fact]
        public void MustReturnSameBuilderFromAddTokenProviderInstance()
        {
            var sut = CreateSut();
            sut.AddTokenProvider(new NullTokenProvider()).Should().BeSameAs(sut);
        }

        [Fact]
        public void MustReturnSameBuilderFromAddTokenProviderFactory()
        {
            var sut = CreateSut();
            sut.AddTokenProvider(() => new NullTokenProvider()).Should().BeSameAs(sut);
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
    }
}
