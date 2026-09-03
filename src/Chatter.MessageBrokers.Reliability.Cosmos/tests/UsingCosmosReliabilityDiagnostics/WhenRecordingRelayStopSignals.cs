using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Diagnostics.Metrics;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityDiagnostics
{
    /// <summary>
    /// The on state of the two instruments that report the Outbox Relay declining to keep republishing: the
    /// undeliverable count, raised when a document violates the Outbox Document Contract, and the suspension count,
    /// raised when the relay stops draining a Lease Token.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenRecordingRelayStopSignals : Testing.Core.Context
    {
        /// <summary>
        /// The undeliverable count carries NO attribute. One document can violate several contract facts at once, and
        /// a single-valued attribute may not claim one of them for a heterogeneous set (ADR-0010 D7) - the full
        /// violation text rides the always-on log instead.
        /// </summary>
        [Fact]
        public void MustCountOneUndeliverableDocumentWithoutClaimingOneViolation()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordUndeliverableDocument();

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainUndeliverableInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(1);
                measurement.Tags.Should().BeEmpty("a document can violate several contract facts at once, so no single-valued attribute is true of the set");
            }
        }

        /// <summary>
        /// A suspension is reported against the Lease Token it was raised for, which is what keeps a suspended lease
        /// distinguishable from an idle one.
        /// </summary>
        [Fact]
        public void MustCountOneDrainSuspensionAgainstItsLease()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordDrainSuspension("lease-7");

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainSuspensionsInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(1);
                measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                leaseToken.Should().Be("lease-7");
            }
        }

        /// <summary>
        /// Enabling ONE of these instruments is a full opt-in: <see cref="CosmosReliabilityDiagnostics.IsEnabled"/>
        /// is the OR across this module's whole surface, so a call site that checks it before doing instrumented
        /// work still takes the instrumented path for an application that subscribed to nothing else.
        /// </summary>
        [Theory]
        [InlineData(CosmosReliabilityDiagnostics.DrainUndeliverableInstrumentName)]
        [InlineData(CosmosReliabilityDiagnostics.DrainSuspensionsInstrumentName)]
        public void MustReportDiagnosticsEnabledForASingleInstrumentOptIn(string instrumentName)
        {
            DeclareInstruments();

            using (new SingleInstrumentMeterScope(CosmosReliabilityDiagnostics.MeterName, instrumentName))
            {
                CosmosReliabilityDiagnostics.IsEnabled.Should().BeTrue();
            }
        }

        /// <summary>
        /// Reads the off-guard so the static surface initialises, and with it publishes its instruments, BEFORE a
        /// listener attaches. A <c>MeterListener</c> replays already-published instruments when it starts, so the
        /// lookup is timing-proof in both orders.
        /// </summary>
        private static void DeclareInstruments() => _ = CosmosReliabilityDiagnostics.IsEnabled;

        /// <summary>
        /// Attaches a .NET <see cref="MeterListener"/> that enables EXACTLY ONE named instrument on one meter, which
        /// is the state <see cref="RecordingMeterScope"/> cannot express: it enables every instrument the meter
        /// publishes. Disposal disables the instrument it enabled, because disposing the .NET listener alone leaves
        /// the instrument <see cref="Instrument.Enabled"/> for every later test in the process.
        /// </summary>
        private sealed class SingleInstrumentMeterScope : IDisposable
        {
            private readonly string _meterName;
            private readonly string _instrumentName;
            private readonly MeterListener _netMeterListener;
            private Instrument _enabledInstrument;
            private bool _disposed;

            public SingleInstrumentMeterScope(string meterName, string instrumentName)
            {
                _meterName = meterName;
                _instrumentName = instrumentName;

                _netMeterListener = new MeterListener
                {
                    InstrumentPublished = EnableWhenNameMatches,
                };

                _netMeterListener.Start();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_enabledInstrument != null)
                {
                    _netMeterListener.DisableMeasurementEvents(_enabledInstrument);
                }

                _netMeterListener.Dispose();
            }

            private void EnableWhenNameMatches(Instrument instrument, MeterListener netMeterListener)
            {
                if (instrument.Meter.Name != _meterName || instrument.Name != _instrumentName)
                {
                    return;
                }

                _enabledInstrument = instrument;
                netMeterListener.EnableMeasurementEvents(instrument);
            }
        }
    }
}
