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
    // The section is bound INTO the default-initialized ServiceBusOptions over a NARROW surface — plain
    // Bind(), no BindNonPublicProperties — and the internal RetryPolicy configuration property is bound
    // EXPLICITLY from its own subsection, so a populated Chatter:Infrastructure:AzureServiceBus:RetryPolicy
    // section yields matching ServiceBusRetryOptions while the internal-set RetryOptions and
    // TokenCredential properties stay UNREACHABLE from configuration. Configuration can disable retry ONLY
    // through the explicit
    // RetryPolicy:NoRetry opt-in: an empty or all-zero RetryPolicy section under a present service-bus
    // section falls back to the SDK default ServiceBusRetryOptions by design, and when the whole
    // service-bus section is absent nothing binds so RetryOptions stays null. The fluent WithNoRetry()
    // / WithExponentialDelay() setters still WIN over a configured RetryPolicy — this module's
    // nullable-sentinel backing fields make an explicit fluent call beat configuration, the opposite
    // of the core Chatter.MessageBrokers builders.
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
            // BY DESIGN: section absent and no fluent setter, so there is nothing to bind,
            // PostConfiguration never runs and no fluent RetryOptions is applied — RetryOptions stays
            // null on the freshly-built options.
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();
            options.RetryOptions.Should().BeNull();
        }

        [Fact]
        public void MustFallBackToSdkDefaultRetryOptionsWhenRetryPolicyAllZeroWithoutNoRetryOptIn()
        {
            // BY DESIGN: the all-zero RetryPolicy section binds, but every zero reads as "not
            // configured" and the section carries no NoRetry opt-in, so each parameter falls back to the
            // SDK default ServiceBusRetryOptions. No branch infers no-retry from all-zero values, so
            // retry CANNOT be disabled this way — only the explicit NoRetry opt-in disables it.
            var defaultOptions = new ServiceBusRetryOptions();

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
            options.RetryOptions.MaxRetries.Should().Be(defaultOptions.MaxRetries);
            options.RetryOptions.MaxRetries.Should().NotBe(0);
            options.RetryOptions.Delay.Should().Be(defaultOptions.Delay);
            options.RetryOptions.MaxDelay.Should().Be(defaultOptions.MaxDelay);
        }

        [Fact]
        public void MustApplyConfiguredRetryPolicyWhenSectionPopulated()
        {
            // A populated RetryPolicy section is HONOURED: MaximumRetryCount maps to MaxRetries and
            // MinimumBackoffInSeconds to Delay. MaximumBackoffInSeconds is left unconfigured, so MaxDelay
            // keeps the SDK default rather than collapsing to zero.
            var defaultOptions = new ServiceBusRetryOptions();

            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "5",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "1",
            });
            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.Mode.Should().Be(ServiceBusRetryMode.Exponential);
            options.RetryOptions.MaxRetries.Should().Be(5);
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(1));
            options.RetryOptions.MaxDelay.Should().Be(defaultOptions.MaxDelay);
        }

        [Fact]
        public void MustDisableRetryWhenRetryPolicyNoRetryOptInConfigured()
        {
            // The explicit NoRetry opt-in is the ONLY way configuration can disable retry. Mirrors the
            // fluent WithNoRetry().
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:NoRetry"] = "true",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.MaxRetries.Should().Be(0);
        }

        [Fact]
        public void MustFallBackToSdkDefaultRetryOptionsWhenRetryPolicySectionEmpty()
        {
            // BY DESIGN: an empty-but-present RetryPolicy section has no children to bind, so every
            // parameter on the bound RetryPolicyConfiguration keeps its zero default, no NoRetry opt-in is
            // present, and the SDK default ServiceBusRetryOptions applies — the same designed fall-through
            // as the all-zero section, never a disabled retry.
            var defaultOptions = new ServiceBusRetryOptions();

            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy"] = string.Empty,
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.Mode.Should().Be(defaultOptions.Mode);
            options.RetryOptions.MaxRetries.Should().Be(defaultOptions.MaxRetries);
            options.RetryOptions.MaxRetries.Should().NotBe(0);
        }

        [Fact]
        public void MustIgnoreConfiguredRetryOptionsWhenRetryPolicyConfigured()
        {
            // A stray RetryOptions key cannot clobber the RetryPolicy-derived value: the narrow bind
            // surface never hands the internal-set RetryOptions property to the binder, so the key is
            // inert. ([JsonIgnore] would NOT have gated the configuration binder — the narrowness does.)
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryOptions:MaxRetries"] = "9",
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "5",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "1",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.MaxRetries.Should().Be(5);
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void MustApplyEveryConfiguredRetryPolicyKeyWhenSectionFullyPopulated()
        {
            // Every documented RetryPolicy key binds through the EXPLICIT RetryPolicy bind:
            // MaximumRetryCount maps to MaxRetries, MinimumBackoffInSeconds to Delay and
            // MaximumBackoffInSeconds to MaxDelay. DeltaBackoffInSeconds binds but is IGNORED — the SDK
            // has no per-attempt delta-backoff knob.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "7",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "2",
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = "45",
                [$"{_sectionName}:RetryPolicy:DeltaBackoffInSeconds"] = "3",
            });
            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.Mode.Should().Be(ServiceBusRetryMode.Exponential);
            options.RetryOptions.MaxRetries.Should().Be(7);
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(2));
            options.RetryOptions.MaxDelay.Should().Be(TimeSpan.FromSeconds(45));
        }

        [Fact]
        public void MustNotReachRetryOptionsWithAConfiguredValueTheSdkRejects()
        {
            // INVARIANT: RetryOptions is UNREACHABLE from configuration by construction — the narrow bind
            // surface never hands the property to the binder. This is the test that distinguishes
            // unreachable from overwritten-afterwards: ServiceBusRetryOptions.MaxRetries validates 0..100
            // in its SETTER, so a widened bind surface raises ArgumentOutOfRangeException from INSIDE the
            // bind, BEFORE any later assignment can overwrite the property. Reaching a normal value and
            // overwriting it afterwards is indistinguishable by final value; reaching an SDK-rejected
            // value is not.
            var defaultOptions = new ServiceBusRetryOptions();

            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryOptions:MaxRetries"] = "500",
            });
            Action build = () => Create(new ServiceCollection(), config).Build();
            build.Should().NotThrow();

            var options = Create(new ServiceCollection(), config).Build();
            options.RetryOptions.MaxRetries.Should().Be(defaultOptions.MaxRetries);
        }

        [Fact]
        public void MustPreferFluentNoRetryOverConfiguredRetryPolicy()
        {
            // This module's precedence rule, the OPPOSITE of the core Chatter.MessageBrokers builders:
            // the nullable-sentinel backing fields distinguish "fluent never called" from "called with
            // the default value", so an explicit fluent call WINS over a populated config section.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "5",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "1",
            });
            var options = Create(new ServiceCollection(), config)
                .WithNoRetry()
                .Build();
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.MaxRetries.Should().Be(0);
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

        // -------------------------------------------------- (F5) explicit fluent value wins over config binding

        [Fact]
        public void MustPreferExplicitDefaultMaxConcurrentCallsOverConfigValue()
        {
            // F5 nullable-backing-field guard: WithMaxConcurrentCalls(1) is the DEFAULT value, but calling it
            // explicitly must still win over a config-bound non-default value. A plain int backing field
            // defaulting to 1 could not distinguish "called with 1" from "never called" and would silently
            // drop the explicit override, leaving the config-bound 5 in place.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:MaxConcurrentCalls"] = "5",
            });
            var options = Create(new ServiceCollection(), config)
                .WithMaxConcurrentCalls(1)
                .Build();
            options.MaxConcurrentCalls.Should().Be(1);
        }

        [Fact]
        public void MustPreserveConfigMaxConcurrentCallsWhenFluentNotCalled()
        {
            // F5: WithMaxConcurrentCalls NOT called leaves the config-bound value untouched — the nullable
            // backing field stays null so no fluent override is applied.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:MaxConcurrentCalls"] = "5",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.MaxConcurrentCalls.Should().Be(5);
        }

        [Fact]
        public void MustPreferExplicitDefaultPrefetchCountOverConfigValue()
        {
            // F5 nullable-backing-field guard: WithPrefetchCount(0) is the DEFAULT value, but calling it
            // explicitly must still win over a config-bound non-default value (10), exactly as
            // MaxConcurrentCalls does.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:PrefetchCount"] = "10",
            });
            var options = Create(new ServiceCollection(), config)
                .WithPrefetchCount(0)
                .Build();
            options.PrefetchCount.Should().Be(0);
        }

        [Fact]
        public void MustPreserveConfigPrefetchCountWhenFluentNotCalled()
        {
            // F5: WithPrefetchCount NOT called leaves the config-bound value untouched.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:PrefetchCount"] = "10",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.PrefetchCount.Should().Be(10);
        }

        // -------------------------------------------------- every public configuration property still binds

        [Fact]
        public void MustBindConfiguredEnableCrossEntityTransactions()
        {
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:EnableCrossEntityTransactions"] = "true",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.EnableCrossEntityTransactions.Should().BeTrue();
        }

        [Fact]
        public void MustBindConfiguredSessionIdleTimeout()
        {
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:SessionIdleTimeout"] = "00:02:00",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.SessionIdleTimeout.Should().Be(TimeSpan.FromMinutes(2));
        }

        [Fact]
        public void MustBindConfiguredMaxSessionLockRenewalDuration()
        {
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:MaxSessionLockRenewalDuration"] = "00:10:00",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.MaxSessionLockRenewalDuration.Should().Be(TimeSpan.FromMinutes(10));
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

        [Fact]
        public void MustIgnoreConfiguredTokenCredentialValue()
        {
            // INVARIANT: the internal-set TokenCredential property is UNREACHABLE from configuration
            // because the narrow bind surface never hands it to the binder. The abstract type alone does
            // NOT make the property safe: it only makes this SCALAR shape a no-op (no converter exists for
            // an abstract type). The NESTED shape is the dangerous one and is covered by
            // MustIgnoreNestedConfiguredTokenCredentialObject. AddTokenProvider is the only way in.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _noSasConnectionString,
                [$"{_sectionName}:TokenCredential"] = "some-credential",
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.TokenCredential.Should().BeNull();
        }

        [Fact]
        public void MustIgnoreNestedConfiguredTokenCredentialObject()
        {
            // The second, dangerous TokenCredential shape: a NESTED configuration object gives the binder
            // children to bind, so a widened bind surface drives it into ACTIVATING the abstract
            // TokenCredential type and raises a raw InvalidOperationException at host start. The narrow
            // bind surface never offers the property to the binder at all, so the nested keys are inert
            // and the credential stays null.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _noSasConnectionString,
                [$"{_sectionName}:TokenCredential:ClientId"] = "some-client-id",
            });
            Action build = () => Create(new ServiceCollection(), config).Build();
            build.Should().NotThrow();

            var options = Create(new ServiceCollection(), config).Build();
            options.TokenCredential.Should().BeNull();
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
