using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Options.UsingServiceBusOptionsBuilder
{
    // Build() is internal (InternalsVisibleTo covers the test assembly). Real ServiceCollection
    // and ConfigurationBuilder().AddInMemoryCollection(...) drive both section-absent and
    // section-present branches.
    //
    // The internal ServiceBusOptions.RetryPolicy config property never binds via
    // section.Get<ServiceBusOptions>() (the binder skips internal setters), so PostConfiguration
    // ALWAYS hits its first branch and Policy is a fresh RetryExponential (RetryPolicy.Default).
    // The all-zero->NoRetry and populated->RetryExponential-mapping branches are therefore dead
    // via config; see the characterization-findings doc. NoRetry / mapped RetryExponential are only
    // reachable through the WithNoRetry() / WithExponentialDelay() fluent setters, which is what the
    // policy tests below pin.
    public class WhenBuilding : Testing.Core.Context
    {
        private const string _sasConnectionString =
            "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dmFsdWU=";
        private const string _noSasConnectionString =
            "Endpoint=sb://example.servicebus.windows.net/;EntityPath=q";
        private const string _sectionName = "Chatter:Infrastructure:AzureServiceBus";

        private static IConfiguration EmptyConfig()
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        private static IConfiguration ConfigWith(IDictionary<string, string> values)
            => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        private static ServiceBusOptionsBuilder Create(IServiceCollection services, IConfiguration configuration)
            => ServiceBusOptionsBuilder.Create(services, configuration);

        [Fact]
        public void MustThrowBareExceptionWhenNoConnectionStringInlineOrConfig()
        {
            var sut = Create(new ServiceCollection(), EmptyConfig());
            Action build = () => sut.Build();
            build.Should().Throw<Exception>()
                 .Which.Message.Should().Be("A connection string is required.");
        }

        [Fact]
        public void MustThrowExactlyBaseExceptionTypeForMissingConnectionString()
        {
            // INVARIANT: the guard throws the bare base System.Exception, not a derived type.
            var sut = Create(new ServiceCollection(), EmptyConfig());
            try
            {
                sut.Build();
                throw new Xunit.Sdk.XunitException("expected Build() to throw");
            }
            catch (Exception ex)
            {
                ex.GetType().Should().Be<Exception>();
            }
        }

        [Fact]
        public void MustUseInlineConnectionString()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();
            options.ConnectionString.Should().Be(_sasConnectionString);
        }

        [Fact]
        public void MustUseConfigConnectionStringWhenNoInline()
        {
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.ConnectionString.Should().Be(_sasConnectionString);
        }

        [Fact]
        public void MustPreferInlineConnectionStringOverConfig()
        {
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = "config-connection-string"
            });
            var options = Create(new ServiceCollection(), config)
                .WithConnectionString(_sasConnectionString)
                .Build();
            options.ConnectionString.Should().Be(_sasConnectionString);
        }

        [Fact]
        public void MustRegisterOptionsAsSingleton()
        {
            var services = new ServiceCollection();
            var options = Create(services, EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();
            var provider = services.BuildServiceProvider();
            provider.GetService<ServiceBusOptions>().Should().BeSameAs(options);
        }

        [Fact]
        public void MustLeavePolicyAsRetryExponentialWhenSectionAbsent()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();
            options.Policy.Should().BeOfType<RetryExponential>();
        }

        [Fact]
        public void MustLeavePolicyAsRetryExponentialWhenRetryPolicySectionAllZero()
        {
            // Pins the dead-branch behavior: even an all-zero RetryPolicy config section does NOT
            // produce NoRetry, because the internal RetryPolicy property never binds.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "0",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "0",
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = "0",
                [$"{_sectionName}:RetryPolicy:DeltaBackoffInSeconds"] = "0",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.Policy.Should().BeOfType<RetryExponential>();
        }

        [Fact]
        public void MustLeavePolicyAsRetryExponentialWhenRetryPolicySectionPopulated()
        {
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "5",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "1",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.Policy.Should().BeOfType<RetryExponential>();
        }

        [Fact]
        public void MustApplyNoRetryPolicyViaFluentSetter()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .WithNoRetry()
                .Build();
            options.Policy.GetType().Name.Should().Be("NoRetry");
        }

        [Fact]
        public void MustApplyExponentialDelayPolicyViaFluentSetter()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .WithExponentialDelay(5, 30, 1, 3)
                .Build();
            options.Policy.Should().BeOfType<RetryExponential>();
        }

        [Fact]
        public void MustOverrideMaxConcurrentCallsWhenDifferingFromDefault()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .WithMaxConcurrentCalls(9)
                .Build();
            options.MaxConcurrentCalls.Should().Be(9);
        }

        [Fact]
        public void MustLeaveMaxConcurrentCallsAtDefaultWhenNotOverridden()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();
            options.MaxConcurrentCalls.Should().Be(1);
        }

        [Fact]
        public void MustOverridePrefetchCountWhenDifferingFromDefault()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .WithPrefetchCount(4)
                .Build();
            options.PrefetchCount.Should().Be(4);
        }

        [Fact]
        public void MustLeavePrefetchCountAtDefaultWhenNotOverridden()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();
            options.PrefetchCount.Should().Be(0);
        }

        [Fact]
        public void MustNotApplyTokenProviderWhenConnectionStringHasSas()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .AddTokenProvider(new MarkerTokenProvider())
                .Build();
            options.TokenProvider.Should().BeOfType<NullTokenProvider>();
        }

        [Fact]
        public void MustApplyTokenProviderWhenConnectionStringLacksSas()
        {
            var marker = new MarkerTokenProvider();
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_noSasConnectionString)
                .AddTokenProvider(marker)
                .Build();
            options.TokenProvider.Should().BeSameAs(marker);
        }

        [Fact]
        public void MustLeaveDefaultNullTokenProviderWhenNoneSupplied()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_noSasConnectionString)
                .Build();
            options.TokenProvider.Should().BeOfType<NullTokenProvider>();
        }

        private sealed class MarkerTokenProvider : ITokenProvider
        {
            public Task<SecurityToken> GetTokenAsync(string appliesTo, TimeSpan timeout)
                => Task.FromResult(new SecurityToken("t", DateTime.Now, "a", string.Empty));
        }
    }
}
