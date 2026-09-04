using Chatter.MessageBrokers.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Configuration.UsingBuiltOptions
{
    public class WhenRegistering : Testing.Core.Context
    {
        [Fact]
        public void MustResolveTheBuiltInstanceFromIOptions()
        {
            var services = new ServiceCollection();
            var builtOptions = new FakeOptions();

            services.AddBuiltOptions(builtOptions);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<FakeOptions>>().Value.Should().BeSameAs(builtOptions);
        }

        [Fact]
        public void MustResolveTheBuiltInstanceFromIOptionsSnapshot()
        {
            var services = new ServiceCollection();
            var builtOptions = new FakeOptions();

            services.AddBuiltOptions(builtOptions);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsSnapshot<FakeOptions>>().Value.Should().BeSameAs(builtOptions);
        }

        [Fact]
        public void MustResolveTheBuiltInstanceFromIOptionsMonitor()
        {
            var services = new ServiceCollection();
            var builtOptions = new FakeOptions();

            services.AddBuiltOptions(builtOptions);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsMonitor<FakeOptions>>().CurrentValue.Should().BeSameAs(builtOptions);
        }

        [Theory]
        [InlineData("SomeOptionsName")]
        [InlineData(null)]
        [InlineData("")]
        public void MustResolveTheBuiltInstanceForAnyOptionsName(string name)
        {
            var services = new ServiceCollection();
            var builtOptions = new FakeOptions();

            services.AddBuiltOptions(builtOptions);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsSnapshot<FakeOptions>>().Get(name).Should().BeSameAs(builtOptions);
        }

        [Fact]
        public void MustReturnANonNullRegistrationWhenRegisteringForChanges()
        {
            var services = new ServiceCollection();

            services.AddBuiltOptions(new FakeOptions());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsMonitor<FakeOptions>>().OnChange((options, name) => { }).Should().NotBeNull();
        }

        [Fact]
        public void MustNotThrowWhenTheChangeRegistrationIsDisposedTwice()
        {
            var services = new ServiceCollection();

            services.AddBuiltOptions(new FakeOptions());

            using var provider = services.BuildServiceProvider();
            var registration = provider.GetRequiredService<IOptionsMonitor<FakeOptions>>().OnChange((options, name) => { });

            registration.Dispose();
            var act = () => registration.Dispose();

            act.Should().NotThrow();
        }

        [Fact]
        public void MustNotResolveAnOrphanedEarlierInstanceFromAnyFacetWhenRegisteredTwice()
        {
            var services = new ServiceCollection();
            var orphanedOptions = new FakeOptions();

            services.AddBuiltOptions(orphanedOptions);
            services.AddBuiltOptions(new FakeOptions());

            using var provider = services.BuildServiceProvider();

            provider.GetServices<IOptions<FakeOptions>>().Should().NotContain(facet => facet.Value == orphanedOptions);
            provider.GetServices<IOptionsSnapshot<FakeOptions>>().Should().NotContain(facet => facet.Value == orphanedOptions);
            provider.GetServices<IOptionsMonitor<FakeOptions>>().Should().NotContain(facet => facet.CurrentValue == orphanedOptions);
        }

        [Fact]
        public void MustResolveTheLastBuiltInstanceFromEveryFacetWhenRegisteredTwice()
        {
            var services = new ServiceCollection();

            services.AddBuiltOptions(new FakeOptions());
            var rebuiltOptions = new FakeOptions();
            services.AddBuiltOptions(rebuiltOptions);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<FakeOptions>>().Value.Should().BeSameAs(rebuiltOptions);
            provider.GetRequiredService<IOptionsSnapshot<FakeOptions>>().Value.Should().BeSameAs(rebuiltOptions);
            provider.GetRequiredService<IOptionsMonitor<FakeOptions>>().CurrentValue.Should().BeSameAs(rebuiltOptions);
        }

        [Fact]
        public void MustResolveTheLastBuiltInstanceFromTheConcreteTypeWhenRegisteredTwice()
        {
            var services = new ServiceCollection();

            services.AddBuiltOptions(new FakeOptions());
            var rebuiltOptions = new FakeOptions();
            services.AddBuiltOptions(rebuiltOptions);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<FakeOptions>().Should().BeSameAs(rebuiltOptions);
        }

        [Fact]
        public void MustResolveTheBuiltInstanceFromTheConcreteType()
        {
            var services = new ServiceCollection();
            var builtOptions = new FakeOptions();

            services.AddBuiltOptions(builtOptions);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<FakeOptions>().Should().BeSameAs(builtOptions);
        }

        [Fact]
        public void MustResolveTheBuiltInstanceFromEveryFacetWhenAddOptionsRanFirst()
        {
            var services = new ServiceCollection();
            var builtOptions = new FakeOptions();

            services.AddOptions();
            services.AddBuiltOptions(builtOptions);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<FakeOptions>>().Value.Should().BeSameAs(builtOptions);
            provider.GetRequiredService<IOptionsSnapshot<FakeOptions>>().Value.Should().BeSameAs(builtOptions);
            provider.GetRequiredService<IOptionsMonitor<FakeOptions>>().CurrentValue.Should().BeSameAs(builtOptions);
        }

        [Fact]
        public void MustResolveTheBuiltInstanceFromEveryFacetWhenAddOptionsRanLast()
        {
            var services = new ServiceCollection();
            var builtOptions = new FakeOptions();

            services.AddBuiltOptions(builtOptions);
            services.AddOptions();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<FakeOptions>>().Value.Should().BeSameAs(builtOptions);
            provider.GetRequiredService<IOptionsSnapshot<FakeOptions>>().Value.Should().BeSameAs(builtOptions);
            provider.GetRequiredService<IOptionsMonitor<FakeOptions>>().CurrentValue.Should().BeSameAs(builtOptions);
        }

        [Fact]
        public void MustResolveTheBuiltInstanceFromIOptionsSnapshotWithinAChildScope()
        {
            var services = new ServiceCollection();
            var builtOptions = new FakeOptions();

            services.AddOptions();
            services.AddBuiltOptions(builtOptions);

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<FakeOptions>>().Value.Should().BeSameAs(builtOptions);
        }

        private class FakeOptions
        {
            public int OutboxProcessingIntervalInMilliseconds { get; set; }
        }
    }
}
