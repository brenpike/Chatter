using Azure.Core;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
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
    // TokenCredential properties stay UNREACHABLE from configuration. TWO configuration paths switch retry
    // off: the intention-revealing RetryPolicy:NoRetry opt-in, and a stated MaximumRetryCount of 0, which
    // binds faithfully to MaxRetries 0. An ABSENT numeric parameter falls back to the SDK default for that
    // parameter; a STATED one is carried to the SDK's own setter, which raises its OWN
    // ArgumentOutOfRangeException naming the SDK member rather than the configuration key — nothing on this
    // path inspects a configured value first, and this module adds NO named build-time validation of its
    // own, BY DECISION: that setter is the authority and already refuses a value it cannot run with during
    // Build(), with nothing yet registered in the service collection (#423). When the
    // whole service-bus section is absent nothing binds, so RetryOptions stays null. The fluent
    // WithNoRetry() / WithExponentialDelay() setters WIN over a configured RetryPolicy — this module's
    // nullable-sentinel backing fields make an explicit fluent call beat configuration, the opposite of the
    // core Chatter.MessageBrokers builders — and because ResolveRetryOptions checks the FLUENT source before
    // it looks at the bound section, a configured section the fluent call discards is never CONSTRUCTED, so
    // none of its values ever reach the SDK's setters.
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

        // INVARIANT: the Azure SDK's OWN property setter is the oracle for every retry bound this builder
        // can produce. Each helper below offers a candidate to the SAME ServiceBusRetryOptions setter
        // CreateExponentialRetryOptions assigns to, so every bound pinned in this file is READ from the SDK
        // at run time instead of restated as a constant here. That is why this module adds no bound of its
        // own: a check here could only repeat the predicate the SDK already applies, and a check that
        // DIVERGED from it would refuse a value the released package accepts.
        private static bool SdkAcceptsRetryCount(int retryCount)
        {
            try
            {
                _ = new ServiceBusRetryOptions { MaxRetries = retryCount };
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        // A backoff the TimeSpan conversion itself cannot express is one the setter can never be offered,
        // so it counts as refused for the search below; the pins assert the setter's own exception type
        // separately, which keeps a TimeSpan-range refusal from being mistaken for an SDK-range one.
        private static bool SdkAcceptsDelaySeconds(double seconds)
        {
            try
            {
                _ = new ServiceBusRetryOptions { Delay = TimeSpan.FromSeconds(seconds) };
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool SdkAcceptsMaxDelaySeconds(double seconds)
        {
            try
            {
                _ = new ServiceBusRetryOptions { MaxDelay = TimeSpan.FromSeconds(seconds) };
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        // The largest MaxRetries the SDK setter accepts, bisected over that setter's own verdict. The setter
        // range-checks, so the values it accepts are contiguous and the bisection is exact. Both premises
        // are asserted, so an SDK that stops bounding MaxRetries fails here LOUDLY rather than yielding a
        // ceiling nothing checked.
        private static int FindSdkMaximumRetryCount()
        {
            SdkAcceptsRetryCount(0).Should().BeTrue("the bisection starts from a retry count the SDK accepts");
            SdkAcceptsRetryCount(int.MaxValue).Should().BeFalse("a first-refused retry count exists only while the SDK bounds MaxRetries");

            var accepted = 0;
            var refused = int.MaxValue;
            while (refused - accepted > 1)
            {
                var midpoint = accepted + ((refused - accepted) / 2);
                if (SdkAcceptsRetryCount(midpoint))
                {
                    accepted = midpoint;
                }
                else
                {
                    refused = midpoint;
                }
            }

            return accepted;
        }

        // Bisects between a backoff the SDK setter ACCEPTS and one it REFUSES, returning the closest pair
        // that straddles that setter's own boundary. Milliseconds are the search unit because
        // TimeSpan.FromSeconds resolves to them, so every probe round-trips through the same conversion
        // CreateExponentialRetryOptions performs and the returned pair is adjacent at that resolution.
        private static (double AcceptedSeconds, double RefusedSeconds) FindBackoffBoundarySeconds(
            Func<double, bool> sdkAccepts,
            long acceptedMilliseconds,
            long refusedMilliseconds)
        {
            sdkAccepts(acceptedMilliseconds / 1000d).Should().BeTrue("the bisection starts from a backoff the SDK accepts");
            sdkAccepts(refusedMilliseconds / 1000d).Should().BeFalse("the bisection starts from a backoff the SDK refuses");

            while (Math.Abs(refusedMilliseconds - acceptedMilliseconds) > 1)
            {
                var midpoint = acceptedMilliseconds + ((refusedMilliseconds - acceptedMilliseconds) / 2);
                if (sdkAccepts(midpoint / 1000d))
                {
                    acceptedMilliseconds = midpoint;
                }
                else
                {
                    refusedMilliseconds = midpoint;
                }
            }

            return (acceptedMilliseconds / 1000d, refusedMilliseconds / 1000d);
        }

        // The pair of configured MinimumBackoffInSeconds values straddling the floor ServiceBusRetryOptions.Delay
        // enforces: the search walks DOWN from the SDK's own default delay towards zero.
        private static (double AcceptedSeconds, double RefusedSeconds) FindSdkDelayFloorSeconds()
            => FindBackoffBoundarySeconds(SdkAcceptsDelaySeconds, (long)new ServiceBusRetryOptions().Delay.TotalMilliseconds, 0);

        // The pair straddling the ceiling the same setter enforces: the search walks UP from that default
        // towards the largest duration TimeSpan can express.
        private static (double AcceptedSeconds, double RefusedSeconds) FindSdkDelayCeilingSeconds()
            => FindBackoffBoundarySeconds(SdkAcceptsDelaySeconds, (long)new ServiceBusRetryOptions().Delay.TotalMilliseconds, (long)TimeSpan.MaxValue.TotalMilliseconds);

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
        public void MustRegisterNothingWhenTheConnectionStringGuardThrows()
        {
            // INVARIANT: AddBuiltOptions runs LAST in Build(), after the connection-string guard, so a
            // failed guard leaves the service collection untouched and no facet can resolve a half-built
            // ServiceBusOptions. Asserted here because the analogous guards were pinned only by the
            // build-time validation tests this branch removed, which would otherwise leave the invariant
            // claimed in the README, CONTEXT and CHANGELOG but tested nowhere.
            var services = new ServiceCollection();

            Action build = () => Create(services, EmptyConfig()).Build();

            build.Should().Throw<Exception>();
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustFailInTheBinderNamingTheKeyWhenAConfiguredRetryValueIsNotConvertible()
        {
            // A key of the wrong TYPE never reaches the Azure SDK's setters: ConfigurationBinder cannot
            // convert it, so Build() throws its InvalidOperationException first. The message is asserted
            // only for the KEY PATH — the framework words the rest of it differently on net8.0 and
            // net10.0, and that wording is not this module's contract.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "oops",
            });

            Action build = () => Create(new ServiceCollection(), config).Build();

            build.Should().Throw<InvalidOperationException>()
                 .Which.Message.Should().Contain($"{_sectionName}:RetryPolicy:MaximumRetryCount");
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
            // BY DESIGN: section absent and no fluent setter, so ResolveRetryOptions — run once at the
            // end of Build(), after every other source is in hand — has no source to select from and
            // returns null, leaving RetryOptions null on the freshly-built options.
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .Build();
            options.RetryOptions.Should().BeNull();
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
            // The explicit NoRetry opt-in is the INTENTION-REVEALING way configuration switches retry off,
            // and it mirrors the fluent WithNoRetry(). It is NOT the only way: a stated MaximumRetryCount of
            // 0 binds faithfully to MaxRetries 0 as well — see
            // MustBindAConfiguredMaximumRetryCountOfZeroToZeroMaxRetries.
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
            // numeric parameter on the bound RetryPolicyConfiguration stays NULL, no NoRetry opt-in is
            // present, and the SDK default ServiceBusRetryOptions applies. This is the proof that ABSENT
            // and STATED are distinguished: the section is present, so the bound RetryPolicyConfiguration
            // is non-null, yet no parameter was stated, so every one of them falls back to the SDK default
            // for that parameter. Nothing on this path inspects a configured value; only a STATED parameter
            // reaches the SDK's own setter.
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

        [Fact]
        public void MustAcceptConfiguredMaximumRetryCountAtTheSdkCeiling()
        {
            // The ceiling is READ from ServiceBusRetryOptions.MaxRetries — the very setter the stated value
            // is assigned to — instead of being restated here, so this pin follows the SDK if the bound ever
            // moves. The boundary belongs to the valid side: the stated value is carried straight through
            // and the setter accepts it. The other side of the same boundary is pinned by
            // MustLetTheSdkRefuseTheFirstConfiguredMaximumRetryCountAboveItsCeiling.
            var sdkCeiling = FindSdkMaximumRetryCount();
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = sdkCeiling.ToString(CultureInfo.InvariantCulture),
            });

            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.MaxRetries.Should().Be(sdkCeiling);
        }

        [Fact]
        public void MustLetTheSdkRefuseTheFirstConfiguredMaximumRetryCountAboveItsCeiling()
        {
            // The FIRST refused retry count, derived by bisecting ServiceBusRetryOptions.MaxRetries' own
            // verdict rather than naming a constant: nothing in this module range-checks a retry count, so
            // the setter refuses it from inside the construction. The refusal is confirmed against the
            // setter here instead of assumed, and it arrives at BUILD time — the service collection is
            // still empty afterwards, so no facet can resolve a half-built ServiceBusOptions and surface
            // the refused value later.
            var firstRefused = FindSdkMaximumRetryCount() + 1;
            SdkAcceptsRetryCount(firstRefused).Should().BeFalse("one above the ceiling is the first retry count the SDK setter refuses");

            var services = new ServiceCollection();
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = firstRefused.ToString(CultureInfo.InvariantCulture),
            });
            var sut = Create(services, config);

            Action build = () => sut.Build();

            build.Should().Throw<ArgumentOutOfRangeException>();
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustLetTheSdkRefuseANegativeConfiguredMaximumRetryCount()
        {
            // The negative side of the same setter's range. A stated -1 reaches MaxRetries because the
            // nullable numerics carry it there — a "greater than zero means configured" ternary would have
            // replaced it with the SDK default silently — and the setter refuses it at BUILD time, leaving
            // the service collection empty.
            SdkAcceptsRetryCount(-1).Should().BeFalse("the SDK setter refuses a negative retry count");

            var services = new ServiceCollection();
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = (-1).ToString(CultureInfo.InvariantCulture),
            });
            var sut = Create(services, config);

            Action build = () => sut.Build();

            build.Should().Throw<ArgumentOutOfRangeException>();
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustBindAConfiguredMaximumRetryCountOfZeroToZeroMaxRetries()
        {
            // A stated MaximumRetryCount of 0 is bound FAITHFULLY: one explicitly written key reaches
            // MaxRetries as 0. That differs from master, which inferred "off" only from an ALL-ZERO
            // four-key section, and from the build-time validation that briefly refused it outright.
            // NoRetry stays the intention-revealing knob for switching retry off, but a stated zero is NOT
            // rewritten into one on the operator's behalf: zero retries is legitimate operator intent
            // (#423).
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "0",
            });

            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.MaxRetries.Should().Be(0);
        }

        [Fact]
        public void MustLetTheSdkRejectAConfiguredMinimumBackoffItCannotRunWith()
        {
            // Nothing inspects a stated backoff before the SDK does: the construction assigns
            // Delay = TimeSpan.FromSeconds(stated) and ServiceBusRetryOptions.Delay decides. The nullable
            // numerics are what carry a stated value that far — a "greater than zero means configured"
            // ternary would have replaced it with the SDK default silently. Both sides of the setter's
            // CEILING are pinned, and the pair is BISECTED out of that setter's own verdict rather than
            // named here, so the pin cannot drift when the SDK moves the bound: the last accepted backoff
            // binds to Delay, and the first refused one throws at BUILD time with the service collection
            // still empty. The floor of the same setter is pinned by
            // MustStraddleTheSdkDelayFloorWithTheConfiguredMinimumBackoff.
            var (acceptedSeconds, refusedSeconds) = FindSdkDelayCeilingSeconds();

            var acceptedConfig = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = acceptedSeconds.ToString(CultureInfo.InvariantCulture),
            });

            var options = Create(new ServiceCollection(), acceptedConfig).Build();

            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(acceptedSeconds));

            var services = new ServiceCollection();
            var refusedConfig = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = refusedSeconds.ToString(CultureInfo.InvariantCulture),
            });
            var sut = Create(services, refusedConfig);

            Action build = () => sut.Build();

            build.Should().Throw<ArgumentOutOfRangeException>();
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustStraddleTheSdkDelayFloorWithTheConfiguredMinimumBackoff()
        {
            // The other bound of the same setter, bisected the same way but walking DOWN from the SDK's own
            // default delay: the smallest backoff ServiceBusRetryOptions.Delay accepts binds through, and
            // the largest one below it — a stated zero, on the SDK measured here — is refused by that setter
            // at BUILD time, leaving nothing registered. Zero is worth pinning because it is the value an
            // operator is most likely to write meaning "no wait", and this module does not translate it
            // into one.
            var (acceptedSeconds, refusedSeconds) = FindSdkDelayFloorSeconds();

            var acceptedConfig = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = acceptedSeconds.ToString(CultureInfo.InvariantCulture),
            });

            var options = Create(new ServiceCollection(), acceptedConfig).Build();

            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(acceptedSeconds));

            var services = new ServiceCollection();
            var refusedConfig = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = refusedSeconds.ToString(CultureInfo.InvariantCulture),
            });
            var sut = Create(services, refusedConfig);

            Action build = () => sut.Build();

            build.Should().Throw<ArgumentOutOfRangeException>();
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustLetTheSdkRefuseANegativeConfiguredMaximumBackoff()
        {
            // MaxDelay carries a DIFFERENT rule from Delay — it is non-negative rather than strictly
            // positive — and the pair is bisected out of that setter's own verdict, so the difference is
            // read from the SDK rather than assumed: a stated zero binds to MaxDelay, while the largest
            // negative backoff below it is refused at BUILD time with the service collection still empty.
            // A cross-field rule would be wrong here for the same reason: see
            // MustNotTreatMinimumBackoffGreaterThanMaximumBackoffAsAViolation.
            var (acceptedSeconds, refusedSeconds) = FindBackoffBoundarySeconds(SdkAcceptsMaxDelaySeconds, 0, -1000);
            refusedSeconds.Should().BeNegative("MaxDelay's boundary is the sign change, not a magnitude");

            var acceptedConfig = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = acceptedSeconds.ToString(CultureInfo.InvariantCulture),
            });

            var options = Create(new ServiceCollection(), acceptedConfig).Build();

            options.RetryOptions.MaxDelay.Should().Be(TimeSpan.FromSeconds(acceptedSeconds));

            var services = new ServiceCollection();
            var refusedConfig = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumBackoffInSeconds"] = refusedSeconds.ToString(CultureInfo.InvariantCulture),
            });
            var sut = Create(services, refusedConfig);

            Action build = () => sut.Build();

            build.Should().Throw<ArgumentOutOfRangeException>();
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustFallBackToTheSdkDefaultBackoffWhenNeitherBackoffIsConfigured()
        {
            // The nullable-semantics proof for the backoffs: the section is populated and bound, but
            // neither backoff key is present, so neither is validated and both SDK defaults stand.
            var defaultOptions = new ServiceBusRetryOptions();
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "5",
            });

            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.MaxRetries.Should().Be(5);
            options.RetryOptions.Delay.Should().Be(defaultOptions.Delay);
            options.RetryOptions.MaxDelay.Should().Be(defaultOptions.MaxDelay);
        }

        [Fact]
        public void MustFallBackToTheSdkDefaultRetryCountWhenMaximumRetryCountIsNotConfigured()
        {
            // The nullable-semantics proof for the retry count: the section is populated and bound, but
            // MaximumRetryCount is absent, so it is not validated, not reported against NoRetry, and the
            // SDK default stands.
            var defaultOptions = new ServiceBusRetryOptions();
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "2",
            });

            var options = Create(new ServiceCollection(), config).Build();

            options.RetryOptions.MaxRetries.Should().Be(defaultOptions.MaxRetries);
            options.RetryOptions.MaxRetries.Should().NotBe(0);
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void MustNotTreatMinimumBackoffGreaterThanMaximumBackoffAsAViolation()
        {
            // The SDK CLAMPS a Delay above MaxDelay while computing each retry delay. That is not a crash,
            // so nothing here refuses it — the built options carry the configured values verbatim.
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
        public void MustStartWithFluentNoRetryWhenTheConfiguredRetryPolicyIsOneTheSdkCannotRunWith()
        {
            // REGRESSION GUARD for FLUENT-FIRST RESOLUTION: ResolveRetryOptions checks the fluent source
            // before it ever looks at the bound section, so a configured section the fluent call discards is
            // never CONSTRUCTED — and a MinimumBackoffInSeconds of 7776000 is a value
            // ServiceBusRetryOptions.Delay rejects from its own setter, so constructing it would block host
            // start on a retry policy the host was never going to use.
            // SCOPE OF THIS GUARD, stated precisely because it is easy to overclaim: it fails if that
            // fluent-first check is removed or reordered BELOW the section branch. It does NOT fail merely
            // because the ResolveRetryOptions CALL moves earlier in Build() — every fluent setter has
            // already run by the time Build() starts, so the early return still fires and this test still
            // passes. The CALL POSITION is pinned by a different set: MustApplyNoRetryOptionsViaFluentSetter,
            // MustApplyExponentialDelayOptionsViaFluentSetter and
            // MustHonourAFluentRetryCountOfZeroWithoutTheNoRetryOptIn, which run with no bound section at all
            // and so fail the moment resolution is moved inside the bind branch.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "101",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "7776000",
            });

            var options = Create(new ServiceCollection(), config)
                .WithNoRetry()
                .Build();

            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.MaxRetries.Should().Be(0);
        }

        [Fact]
        public void MustStartWithFluentExponentialDelayWhenTheConfiguredRetryPolicyIsOneTheSdkCannotRunWith()
        {
            // The same FLUENT-FIRST RESOLUTION guard through the other fluent setter: a VALID fluent
            // exponential policy stands and the configured section it overrides is never constructed, so its
            // 7776000-second minimum backoff never reaches the SDK's Delay setter. The scope note on the
            // test above applies here too — this pins the fluent-first check, not the position of the
            // ResolveRetryOptions call.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "101",
                [$"{_sectionName}:RetryPolicy:MinimumBackoffInSeconds"] = "7776000",
            });

            var options = Create(new ServiceCollection(), config)
                .WithExponentialDelay(5, 30, 1, 3)
                .Build();

            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.Mode.Should().Be(ServiceBusRetryMode.Exponential);
            options.RetryOptions.MaxRetries.Should().Be(5);
            options.RetryOptions.Delay.Should().Be(TimeSpan.FromSeconds(1));
            options.RetryOptions.MaxDelay.Should().Be(TimeSpan.FromSeconds(30));
        }

        [Fact]
        public void MustStartWithFluentNoRetryWhenTheConfiguredRetryPolicyIsOneTheBinderCannotConvert()
        {
            // REGRESSION GUARD for the BIND sitting behind the fluent-first check: a configured RetryPolicy
            // section the fluent call discards is never BOUND, so a MaximumRetryCount the
            // ConfigurationBinder cannot convert never raises its InvalidOperationException on a section the
            // host was never going to use. This is strictly stronger than the never-CONSTRUCTED guards
            // above: a non-convertible scalar fails inside the bind itself, before any construction, so it
            // fails while the bind still runs unconditionally and passes only once the bind is gated too.
            // Without a fluent override the same key still fails the binder — see
            // MustFailInTheBinderNamingTheKeyWhenAConfiguredRetryValueIsNotConvertible.
            var config = ConfigWith(new Dictionary<string, string>
            {
                [$"{_sectionName}:ConnectionString"] = _sasConnectionString,
                [$"{_sectionName}:RetryPolicy:MaximumRetryCount"] = "oops",
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
        public void MustHonourAFluentRetryCountOfZeroWithoutTheNoRetryOptIn()
        {
            // A zero stated outright in code is bound faithfully to MaxRetries 0. There is no asymmetry
            // left for this to document: a configured MaximumRetryCount of 0 now binds the same way (see
            // MustBindAConfiguredMaximumRetryCountOfZeroToZeroMaxRetries), so both paths honour a stated
            // zero and NoRetry / WithNoRetry remains the intention-revealing knob on each.
            var options = Create(new ServiceCollection(), EmptyConfig())
                .WithConnectionString(_sasConnectionString)
                .WithExponentialDelay(0, 30, 1, 3)
                .Build();

            options.RetryOptions.Should().NotBeNull();
            options.RetryOptions.MaxRetries.Should().Be(0);
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
