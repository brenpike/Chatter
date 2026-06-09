using Azure.Core;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading;
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
    // ALWAYS hits its first branch and RetryOptions is a fresh default ServiceBusRetryOptions. The
    // all-zero->MaxRetries=0 and populated->Exponential-mapping branches are therefore dead via
    // config; see the characterization-findings doc. The MaxRetries=0 / mapped Exponential options
    // are only reachable through the WithNoRetry() / WithExponentialDelay() fluent setters, which is
    // what the policy tests below pin.
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
        public void MustLeaveRetryOptionsUnsetWhenSectionAbsent()
        {
            // Section absent and no fluent setter: PostConfiguration never runs and no fluent
            // RetryOptions is applied, so RetryOptions stays null on the freshly-built options.
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();
            options.RetryOptions.Should().BeNull();
        }

        [Fact]
        public void MustLeaveRetryOptionsAtDefaultExponentialWhenRetryPolicySectionAllZero()
        {
            // Pins the dead-branch behavior: even an all-zero RetryPolicy config section does NOT
            // produce a MaxRetries=0 options, because the internal RetryPolicy property never binds,
            // so PostConfiguration takes its first branch and RetryOptions is a fresh default
            // ServiceBusRetryOptions (Exponential mode, the SDK default MaxRetries of 3).
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "0",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "0",
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = "0",
                [$"{_sectionName}:RetryPolicy:DeltaBackoffInSeconds"] = "0",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.Mode.Should().Be(ServiceBusRetryMode.Exponential);
            options.RetryOptions.MaxRetries.Should().Be(new ServiceBusRetryOptions().MaxRetries);
        }

        [Fact]
        public void MustLeaveRetryOptionsAtDefaultWhenRetryPolicySectionPopulated()
        {
            // Pins finding #2: a populated RetryPolicy section is SILENTLY IGNORED because the
            // internal RetryPolicy property never binds, so PostConfiguration takes its first branch
            // and RetryOptions is the default ServiceBusRetryOptions — NOT options built from the
            // supplied MaximumRetryCount=5 / MinimumBackoffInSeconds=1. The supplied values are pinned
            // as ignored by comparing the resulting options' observable parameters against a fresh
            // default ServiceBusRetryOptions and confirming they do NOT reflect the config.
            var defaultOptions = new ServiceBusRetryOptions();

            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "5",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "1",
            });
            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.Should().NotBeNull();
            // The supplied MinimumBackoffInSeconds=1 is IGNORED: the resulting options' Delay stays at
            // the SDK default, never the requested 1s.
            options.RetryOptions.Delay.Should().Be(defaultOptions.Delay);
            options.RetryOptions.Delay.Should().NotBe(TimeSpan.FromSeconds(1));
            // Every observable parameter matches a fresh default ServiceBusRetryOptions: the populated
            // config produced exactly the default options, confirming it was silently ignored.
            options.RetryOptions.Mode.Should().Be(defaultOptions.Mode);
            options.RetryOptions.MaxRetries.Should().Be(defaultOptions.MaxRetries);
            options.RetryOptions.MaxDelay.Should().Be(defaultOptions.MaxDelay);
        }

        [Fact]
        public void MustApplyNoRetryOptionsViaFluentSetter()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .WithNoRetry()
                .Build();
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.MaxRetries.Should().Be(0);
        }

        [Fact]
        public void MustApplyExponentialDelayOptionsViaFluentSetter()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .WithExponentialDelay(5, 30, 1, 3)
                .Build();
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.Mode.Should().Be(ServiceBusRetryMode.Exponential);
            options.RetryOptions.MaxRetries.Should().Be(5);
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(1));
            options.RetryOptions.MaxDelay.Should().Be(TimeSpan.FromSeconds(30));
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
        public void MustNotApplyTokenCredentialWhenConnectionStringHasSas()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .AddTokenProvider(new MarkerTokenCredential())
                .Build();
            options.TokenCredential.Should().BeNull();
        }

        [Fact]
        public void MustApplyTokenCredentialWhenConnectionStringLacksSas()
        {
            var marker = new MarkerTokenCredential();
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_noSasConnectionString)
                .AddTokenProvider(marker)
                .Build();
            options.TokenCredential.Should().BeSameAs(marker);
        }

        [Fact]
        public void MustLeaveTokenCredentialNullWhenNoneSupplied()
        {
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_noSasConnectionString)
                .Build();
            options.TokenCredential.Should().BeNull();
        }

        [Fact]
        public void MustApplyTokenCredentialWhenConnectionStringHasKeyNameButNoSecret()
        {
            // Regression: SAS detection must PARSE the connection string fields, not substring-match the
            // raw string. A connection string carrying SharedAccessKeyName (the key NAME) but no actual
            // SharedAccessKey/SharedAccessSignature secret is NOT SAS-authenticated — it is intended to
            // pair with a TokenCredential for AAD. A naive IndexOf("SharedAccessKey") matches the
            // SharedAccessKeyName key and would falsely drop the credential.
            const string keyNameOnlyConnectionString =
                "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k";
            var marker = new MarkerTokenCredential();
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(keyNameOnlyConnectionString)
                .AddTokenProvider(marker)
                .Build();
            options.TokenCredential.Should().BeSameAs(marker);
        }

        private sealed class MarkerTokenCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new AccessToken("t", DateTimeOffset.MaxValue);

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(new AccessToken("t", DateTimeOffset.MaxValue));
        }
    }
}
