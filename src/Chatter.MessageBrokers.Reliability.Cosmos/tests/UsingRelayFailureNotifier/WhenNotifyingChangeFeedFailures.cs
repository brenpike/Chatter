using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingRelayFailureNotifier
{
    /// <summary>
    /// The #361 observability sink for a faulted change feed: the notifier the Outbox Relay hosts hand to the Cosmos
    /// SDK's error-notification seam, which is the only channel carrying an SDK-side lease fault together with the
    /// Lease Token the relay core never sees.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenNotifyingChangeFeedFailures
    {
        // Built ONCE, at class-initialisation time, so the off-state allocation probe below measures the notifier and
        // not the allocation of the fault handed to it.
        private static readonly ChangeFeedProbeException _changeFeedFault = new ChangeFeedProbeException("the change feed faulted");

        // The notifier of an application that wired no logger, built ONCE for the same reason.
        private static readonly RelayFailureNotifier _loggerlessNotifier = new RelayFailureNotifier(logger: null);

        /// <summary>
        /// A faulted change feed is counted against the Lease Token it faulted under, so an operator reading the drain
        /// failures by lease can see WHICH partition range stopped advancing.
        /// </summary>
        [Fact]
        public async Task MustCountTheFaultAgainstItsLeaseWhileTheInstrumentIsEnabled()
        {
            var notifier = new RelayFailureNotifier(logger: null);

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                await notifier.OnChangeFeedErrorAsync("lease-7", _changeFeedFault);

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainFailuresInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(1);
                measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                leaseToken.Should().Be("lease-7");
                measurement.TryGetTag(ChatterTelemetryTags.ErrorType, out var errorType).Should().BeTrue();
                errorType.Should().Be(typeof(ChangeFeedProbeException).FullName);
            }
        }

        /// <summary>
        /// The always-on channel. Metrics are opt-in, so an application that subscribed to no meter would be left as
        /// silent as the defect #361 describes; the log is what makes a stalled lease visible with no opt-in at all.
        /// </summary>
        [Fact]
        public async Task MustLogTheFaultOnceAtErrorCarryingTheLeaseToken()
        {
            var logger = new RecordingLogger();
            var notifier = new RelayFailureNotifier(logger);

            await notifier.OnChangeFeedErrorAsync("lease-7", _changeFeedFault);

            var entry = logger.Entries.Should().ContainSingle().Subject;

            entry.Level.Should().Be(LogLevel.Error);
            entry.Message.Should().Contain("lease-7");
            entry.Exception.Should().BeSameAs(_changeFeedFault);
        }

        /// <summary>An <see cref="ILogger"/> recorder that captures the rendered message of each log call.</summary>
        private sealed class RecordingLogger : ILogger
        {
            public List<(LogLevel Level, string Message, Exception Exception)> Entries { get; } = new List<(LogLevel, string, Exception)>();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception), exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();

                public void Dispose()
                {
                }
            }
        }

        /// <summary>
        /// The can-never-wedge-the-pump guarantee. The Cosmos SDK invokes this delegate ON the change-feed pump, so a
        /// throw out of it would be a brand-new stall of exactly the class #361 exists to close: observability may
        /// never break delivery, not even when the application's own logging sink is the thing that is broken.
        /// </summary>
        [Fact]
        public async Task MustNotThrowWhenTheSuppliedLoggerItselfThrows()
        {
            var notifier = new RelayFailureNotifier(new ThrowingLogger());

            Func<Task> notifying = () => notifier.OnChangeFeedErrorAsync("lease-7", _changeFeedFault);

            await notifying.Should().NotThrowAsync();
        }

        /// <summary>An <see cref="ILogger"/> whose sink is broken, as a misconfigured application's can be.</summary>
        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) => throw new InvalidOperationException("the logging sink is broken");

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => throw new InvalidOperationException("the logging sink is broken");
        }

        /// <summary>
        /// The off state: an application that subscribed to no meter and wired no logger pays the record method's own
        /// boolean guard and nothing else — no lease-token tag, no error type and no <c>TagList</c> (ADR-0010 R1).
        /// </summary>
        [Fact]
        public void MustBuildNoTagWhileNothingIsOptedInto()
        {
            CosmosReliabilityDiagnostics.IsEnabled.Should().BeFalse();

            var measurement = GuardCostProbe.Measure(NotifyOneChangeFeedFault);

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no attribute may be built while off: " + measurement);
        }

        /// <summary>
        /// A logger is OPTIONAL, so an application that wired none gets a silent no-op that still hands the pump a
        /// completed task rather than a null one — the notifier is awaited by the SDK on the change-feed pump.
        /// </summary>
        [Fact]
        public void MustBeASilentNoOpWhenNoLoggerIsSupplied()
        {
            Task notifying = _loggerlessNotifier.OnChangeFeedErrorAsync("lease-7", _changeFeedFault);

            notifying.Should().NotBeNull();
            notifying.IsCompletedSuccessfully.Should().BeTrue("the notification never blocks the change-feed pump it is invoked on");
        }

        /// <summary>Notifies one change-feed fault through a notifier that has no logger, as the probe measures it.</summary>
        private static void NotifyOneChangeFeedFault() => _ = _loggerlessNotifier.OnChangeFeedErrorAsync("lease-0", _changeFeedFault);

        // The fault a deliberately-failed change feed carries, so the error-type assertion names a type that exists for
        // no other reason.
        private sealed class ChangeFeedProbeException : Exception
        {
            public ChangeFeedProbeException(string message)
                : base(message)
            {
            }
        }
    }
}
