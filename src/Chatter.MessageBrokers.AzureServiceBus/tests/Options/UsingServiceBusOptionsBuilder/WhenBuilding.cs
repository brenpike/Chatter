using Azure.Core;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
            // as the all-zero section, never a disabled retry. This outcome DEPENDS on that "greater than
            // zero means configured" fall-through: the section is present, so the bound
            // RetryPolicyConfiguration is non-null and every parameter must fall back individually.
            var defaultOptions = new ServiceBusRetryOptions();

            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy"] = string.Empty,
            });
            var options = Create(new ServiceCollection(), config).Build();
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.Mode.Should().Be(defaultOptions.Mode);
            options.RetryOptions.Mode.Should().Be(ServiceBusRetryMode.Exponential);
            options.RetryOptions.MaxRetries.Should().Be(defaultOptions.MaxRetries);
            options.RetryOptions.MaxRetries.Should().NotBe(0);
            options.RetryOptions.Delay.Should().Be(defaultOptions.Delay);
            options.RetryOptions.MaxDelay.Should().Be(defaultOptions.MaxDelay);
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

        [Theory]
        [InlineData("101")]
        [InlineData("500")]
        public void MustThrowNamingMaximumRetryCountWhenConfiguredAboveTheSdkCeiling(string configuredRetryCount)
        {
            // The RetryPolicy-derived retry count reaches ServiceBusRetryOptions.MaxRetries, whose SETTER
            // validates 0..100 and raises a bare ArgumentOutOfRangeException naming only the SDK property.
            // The guarded construction validates BEFORE the setter and names the configured knob instead.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = configuredRetryCount,
            });

            Action build = () => Create(new ServiceCollection(), config).Build();

            build.Should().Throw<ServiceBusRetryOptionsValidationException>()
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MaximumRetryCount)}*")
                .WithMessage($"*{configuredRetryCount}*");
        }

        [Theory]
        [InlineData("1e300")]
        [InlineData("Infinity")]
        public void MustThrowNamingMinimumBackoffInSecondsWhenConfiguredOutsideTimeSpanRange(string configuredBackoff)
        {
            // A configured backoff greater than zero reaches TimeSpan.FromSeconds, which raises a bare
            // OverflowException for a number beyond TimeSpan's range and for positive infinity. The guarded
            // construction validates the number first and names the configured knob.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = configuredBackoff,
            });

            Action build = () => Create(new ServiceCollection(), config).Build();

            build.Should().Throw<ServiceBusRetryOptionsValidationException>()
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MinimumBackoffInSeconds)}*");
        }

        [Fact]
        public void MustNameEveryInvalidRetryValueInOneFailureWhenSeveralConfiguredValuesAreInvalid()
        {
            // ONE failure naming every offending knob. An operator who corrected one value, redeployed and
            // only then discovered the next would pay a deployment per invalid value.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "500",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "1e300",
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = "Infinity",
            });

            Action build = () => Create(new ServiceCollection(), config).Build();

            build.Should().Throw<ServiceBusRetryOptionsValidationException>()
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MaximumRetryCount)}*")
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MinimumBackoffInSeconds)}*")
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MaximumBackoffInSeconds)}*")
                .Which.Violations.Should().HaveCount(3);
        }

        [Fact]
        public void MustAcceptConfiguredMaximumRetryCountAtTheSdkCeiling()
        {
            // 100 is the ceiling ServiceBusRetryOptions.MaxRetries accepts in its own setter, so the boundary
            // belongs to the valid side and the guard must let it through.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "100",
            });

            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.MaxRetries.Should().Be(100);
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("-1")]
        [InlineData("-Infinity")]
        public void MustFallBackToTheSdkDefaultBackoffWhenConfiguredValueIsNotGreaterThanZero(string configuredBackoff)
        {
            // DELIBERATE ASYMMETRY with the fluent path, do not harmonise it: configuration honours a
            // parameter only when it is greater than zero, so a NaN or negative number reads as "not
            // configured" and the SDK default stands rather than becoming a violation. The fluent path throws
            // on the same negative because a caller who passes it stated a value outright.
            var defaultOptions = new ServiceBusRetryOptions();
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = configuredBackoff,
            });

            Action build = () => Create(new ServiceCollection(), config).Build();
            build.Should().NotThrow();

            var options = Create(new ServiceCollection(), config).Build();
            options.RetryOptions.Delay.Should().Be(defaultOptions.Delay);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        public void MustNotDeriveZeroMaxRetriesFromConfigurationWithoutTheNoRetryOptIn(string configuredRetryCount)
        {
            // MaxRetries = 0 stays reachable ONLY through the NoRetry / WithNoRetry opt-in. A configured
            // count that is not greater than zero reads as "not configured", so the SDK default stands even
            // when the rest of the section IS populated — retry can never be switched off by inference.
            var defaultOptions = new ServiceBusRetryOptions();
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = configuredRetryCount,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "2",
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = "45",
            });

            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.MaxRetries.Should().Be(defaultOptions.MaxRetries);
            options.RetryOptions.MaxRetries.Should().NotBe(0);
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(2));
            options.RetryOptions.MaxDelay.Should().Be(TimeSpan.FromSeconds(45));
        }

        [Fact]
        public void MustNotTreatMinimumBackoffGreaterThanMaximumBackoffAsAViolation()
        {
            // The SDK CLAMPS a Delay above MaxDelay while computing each retry delay. That is not a crash, so
            // it must never become a violation — the built options carry the configured values verbatim.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "5",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "60",
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = "5",
            });

            Action build = () => Create(new ServiceCollection(), config).Build();
            build.Should().NotThrow();

            var options = Create(new ServiceCollection(), config).Build();
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(60));
            options.RetryOptions.MaxDelay.Should().Be(TimeSpan.FromSeconds(5));
        }

        [Theory]
        [InlineData("7776000")]
        [InlineData("4294968")]
        public void MustThrowNamingMaximumBackoffInSecondsWhenConfiguredAboveTheRuntimeDelayCeiling(string configuredBackoff)
        {
            // MaxDelay's own setter accepts any non-negative TimeSpan, so 90 days is representable, settable
            // and well inside TimeSpan's range. Every retry wait the SDK computes is CLAMPED to MaxDelay and
            // then waited out on a delay timer, which rejects anything above uint.MaxValue - 1 milliseconds —
            // so without this bound the configuration passes the build and throws ArgumentOutOfRangeException
            // from the retry path instead of returning the transient Service Bus failure it was retrying.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = configuredBackoff,
            });

            Action build = () => Create(new ServiceCollection(), config).Build();

            build.Should().Throw<ServiceBusRetryOptionsValidationException>()
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MaximumBackoffInSeconds)}*")
                .WithMessage($"*{configuredBackoff}*");
        }

        [Theory]
        [InlineData("301")]
        [InlineData("7776000")]
        public void MustThrowNamingMinimumBackoffInSecondsWhenTheSdkRejectsTheResultingDelay(string configuredBackoff)
        {
            // ServiceBusRetryOptions.Delay validates its OWN accepted range inside its setter — today an
            // upper bound of five minutes — and raises a bare ArgumentOutOfRangeException naming only
            // 'Delay', a member no operator supplied. The guard offers each backoff to that setter before
            // building, so the rejection is reported against the configured knob in the aggregated failure.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = configuredBackoff,
            });

            Action build = () => Create(new ServiceCollection(), config).Build();

            build.Should().Throw<ServiceBusRetryOptionsValidationException>()
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MinimumBackoffInSeconds)}*")
                .WithMessage($"*{configuredBackoff}*");
        }

        [Fact]
        public void MustAcceptConfiguredMaximumBackoffAtTheRuntimeDelayCeiling()
        {
            // 4294967 seconds is the highest whole second a delay timer can wait out, so the boundary belongs
            // to the valid side and the guard must let it through.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = "4294967",
            });

            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.MaxDelay.Should().Be(TimeSpan.FromSeconds(4294967));
        }

        // The ceiling this guard enforces is only worth anything while it matches what the runtime accepts.
        // Timer.Change shares its limit with the Task.Delay the SDK waits each retry out on, so pin the
        // boundary against it: the accepted maximum must still be accepted there, and one second more must be
        // what the runtime itself rejects. This test fails if a future runtime moves the limit, rather than
        // letting the constant drift away from the sink.
        [Fact]
        public void MustEnforceTheSameBackoffCeilingTheRuntimeAccepts()
        {
            using var timer = new Timer(_ => { });

            Action atTheCeiling = () => timer.Change(TimeSpan.FromSeconds(4294967), TimeSpan.FromMilliseconds(-1));
            Action aboveTheCeiling = () => timer.Change(TimeSpan.FromSeconds(4294968), TimeSpan.FromMilliseconds(-1));

            atTheCeiling.Should().NotThrow();
            aboveTheCeiling.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void MustNameBothBackoffsInOneFailureWhenNeitherCanBeWaitedOut()
        {
            // ONE failure naming every offending knob, exactly as the existing aggregation guarantees: the
            // minimum is rejected by the SDK's own Delay range and the maximum by the delay-timer ceiling, and
            // an operator must see both before redeploying.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "7776000",
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = "7776000",
            });

            Action build = () => Create(new ServiceCollection(), config).Build();

            build.Should().Throw<ServiceBusRetryOptionsValidationException>()
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MinimumBackoffInSeconds)}*")
                .WithMessage($"*{nameof(RetryPolicyConfiguration.MaximumBackoffInSeconds)}*")
                .Which.Violations.Should().HaveCount(2);
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

        [Fact]
        public void MustResolveTheBuiltOptionsFromEveryOptionsFacet()
        {
            // ADDED CAPABILITY, not a defect fix: this builder never registered a Configure<ServiceBusOptions>, so
            // there was no second, half-configured instance to remove. What the facets resolved instead was a
            // framework-created all-default ServiceBusOptions with a null ConnectionString. Registering the facets
            // extends the built-options invariant to Service Bus so it reads "one options instance, everywhere"
            // rather than "everywhere except Service Bus".
            var services = new ServiceCollection();

            var options = Create(services, EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<ServiceBusOptions>().Should().BeSameAs(options);
            provider.GetRequiredService<IOptions<ServiceBusOptions>>().Value.Should().BeSameAs(options);
            provider.GetRequiredService<IOptionsSnapshot<ServiceBusOptions>>().Value.Should().BeSameAs(options);
            provider.GetRequiredService<IOptionsMonitor<ServiceBusOptions>>().CurrentValue.Should().BeSameAs(options);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromEveryOptionsFacetWhenAddOptionsRanBeforeTheBuilder()
        {
            // AddOptions() stands in for the host, which registers the OPEN generic options descriptors. The built
            // options are registered against the CLOSED generics, which win regardless of registration order — proven
            // here ACROSS the assembly boundary, since the facet type lives in Chatter.MessageBrokers.
            var services = new ServiceCollection();
            services.AddOptions();

            var options = Create(services, EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<ServiceBusOptions>().Should().BeSameAs(options);
            provider.GetRequiredService<IOptions<ServiceBusOptions>>().Value.Should().BeSameAs(options);
            provider.GetRequiredService<IOptionsSnapshot<ServiceBusOptions>>().Value.Should().BeSameAs(options);
            provider.GetRequiredService<IOptionsMonitor<ServiceBusOptions>>().CurrentValue.Should().BeSameAs(options);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromEveryOptionsFacetWhenAddOptionsRanAfterTheBuilder()
        {
            // The other order: a host that calls AddOptions() after the Chatter registration must not push the
            // open generic descriptors back in front of the built options.
            var services = new ServiceCollection();

            var options = Create(services, EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();

            services.AddOptions();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<ServiceBusOptions>().Should().BeSameAs(options);
            provider.GetRequiredService<IOptions<ServiceBusOptions>>().Value.Should().BeSameAs(options);
            provider.GetRequiredService<IOptionsSnapshot<ServiceBusOptions>>().Value.Should().BeSameAs(options);
            provider.GetRequiredService<IOptionsMonitor<ServiceBusOptions>>().CurrentValue.Should().BeSameAs(options);
        }

        [Fact]
        public void MustCarryTheFullyBuiltStateOnEveryOptionsFacet()
        {
            // The facets must hand out the FULLY built instance, not a half-built one: the configured connection
            // string, a fluent-sentinel override that beat configuration, and the guarded RetryPolicy-derived retry
            // options all have to be visible through them. This is what proves the facet registration sits after
            // everything that shapes the instance.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:MaxConcurrentCalls"] = "5",
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "7",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "2",
            });
            var services = new ServiceCollection();

            Create(services, config).WithMaxConcurrentCalls(1).Build();

            using var provider = services.BuildServiceProvider();

            AssertCarriesTheFullyBuiltState(provider.GetRequiredService<IOptions<ServiceBusOptions>>().Value);
            AssertCarriesTheFullyBuiltState(provider.GetRequiredService<IOptionsSnapshot<ServiceBusOptions>>().Value);
            AssertCarriesTheFullyBuiltState(provider.GetRequiredService<IOptionsMonitor<ServiceBusOptions>>().CurrentValue);
        }

        private static void AssertCarriesTheFullyBuiltState(ServiceBusOptions options)
        {
            options.ConnectionString.Should().Be(_sasConnectionString);
            options.MaxConcurrentCalls.Should().Be(1);
            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.Mode.Should().Be(ServiceBusRetryMode.Exponential);
            options.RetryOptions.MaxRetries.Should().Be(7);
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(2));
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
