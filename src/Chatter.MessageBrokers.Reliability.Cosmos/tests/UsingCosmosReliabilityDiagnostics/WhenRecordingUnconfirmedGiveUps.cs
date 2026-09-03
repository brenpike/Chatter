using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityDiagnostics
{
    /// <summary>
    /// The published-unconfirmed give-up count: what an application that opted into the
    /// <see cref="CosmosReliabilityDiagnostics.MeterName"/> scope receives when the Outbox Relay stops
    /// re-publishing an Outbox Document whose brokered message was published but never confirmed delivered.
    /// </summary>
    /// <remarks>
    /// This is a SEPARATE instrument from the poisoned count, and these tests pin that separation in both
    /// directions. The two are different facts about different halves of a drain: a poisoned document was never
    /// published, an unconfirmed one WAS. Folding the second onto the first would make the name
    /// <c>chatter.messaging.outbox.drain.poisoned</c> false for half its measurements (ADR-0010 D4), and would
    /// start emitting on that instrument for an application that opted into the Poison Policy — which is off by
    /// default — silently changing what every dashboard summing it means.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenRecordingUnconfirmedGiveUps : Testing.Core.Context
    {
        /// <summary>
        /// A give-up is counted against the Lease Token the document kept failing to confirm under, so a
        /// partition whose progress is being bought by leaving messages unconfirmed reads against the same
        /// dimension as its batch progress.
        /// </summary>
        [Fact]
        public void MustCountOneUnconfirmedGiveUpAgainstItsLease()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordUnconfirmedGiveUp("lease-4");

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.UnconfirmedGiveUpsInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(1);
                measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                leaseToken.Should().Be("lease-4");
            }
        }

        /// <summary>
        /// The unit the give-up is counted in: DOCUMENTS, the same unit as the poisoned count, because both
        /// count documents the Outbox Relay stopped working on rather than attempts it made.
        /// </summary>
        [Fact]
        public void MustDeclareTheUnconfirmedGiveUpInstrumentOnTheModuleMeter()
        {
            DeclareInstruments();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.UnconfirmedGiveUpsInstrumentName, out var unconfirmedGiveUps).Should().BeTrue();
                unconfirmedGiveUps.Unit.Should().Be("{document}");
            }
        }

        /// <summary>
        /// An application that enabled ONLY the give-up count receives no measurement on the poisoned count,
        /// even though the Outbox Relay recorded one: the two give-up kinds are never conflated.
        /// </summary>
        [Fact]
        public void MustLeaveThePoisonedCountSilentWhileOnlyTheGiveUpCountIsEnabled()
        {
            DeclareInstruments();

            using (var meterScope = new SingleInstrumentRecordingScope(CosmosReliabilityDiagnostics.MeterName, CosmosReliabilityDiagnostics.UnconfirmedGiveUpsInstrumentName))
            {
                RecordOneUnconfirmedGiveUpAndOnePoisonedDocument();

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.UnconfirmedGiveUpsInstrumentName).Should().ContainSingle();
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.PoisonedDocumentsInstrumentName).Should().BeEmpty();
            }
        }

        /// <summary>The same separation in the other direction, so neither instrument can carry the other's fact.</summary>
        [Fact]
        public void MustLeaveTheGiveUpCountSilentWhileOnlyThePoisonedCountIsEnabled()
        {
            DeclareInstruments();

            using (var meterScope = new SingleInstrumentRecordingScope(CosmosReliabilityDiagnostics.MeterName, CosmosReliabilityDiagnostics.PoisonedDocumentsInstrumentName))
            {
                RecordOneUnconfirmedGiveUpAndOnePoisonedDocument();

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.PoisonedDocumentsInstrumentName).Should().ContainSingle();
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.UnconfirmedGiveUpsInstrumentName).Should().BeEmpty();
            }
        }

        /// <summary>
        /// The off state: this brake is ALWAYS ON in the Outbox Relay, so an application that subscribed to
        /// nothing reaches this record method on every give-up and must pay one boolean read for it — no lease
        /// token and no <c>TagList</c> is built (ADR-0010 R1).
        /// </summary>
        [Fact]
        public void MustBuildNoTagWhileNothingIsOptedInto()
        {
            CosmosReliabilityDiagnostics.IsEnabled.Should().BeFalse();

            var measurement = GuardCostProbe.Measure(RecordOneUnconfirmedGiveUp);

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no attribute may be built while off: " + measurement);
        }

        /// <summary>Drives the record method once, as the Outbox Relay does for a document it stops re-publishing.</summary>
        private static void RecordOneUnconfirmedGiveUp() => CosmosReliabilityDiagnostics.RecordUnconfirmedGiveUp("lease-0");

        /// <summary>Drives BOTH give-up record methods once, so a conflated instrument would be caught either way.</summary>
        private static void RecordOneUnconfirmedGiveUpAndOnePoisonedDocument()
        {
            CosmosReliabilityDiagnostics.RecordUnconfirmedGiveUp("lease-4");
            CosmosReliabilityDiagnostics.RecordPoisonedDocument("lease-4");
        }

        /// <summary>
        /// Reads the off-guard so the static surface initialises, and with it publishes its instruments, BEFORE a
        /// listener attaches. A <c>MeterListener</c> replays already-published instruments when it starts, so the
        /// lookup is timing-proof in both orders.
        /// </summary>
        private static void DeclareInstruments() => _ = CosmosReliabilityDiagnostics.IsEnabled;

        /// <summary>
        /// Attaches a .NET <see cref="MeterListener"/> that enables EXACTLY ONE named instrument on one meter and
        /// records what it publishes, which is the state <see cref="RecordingMeterScope"/> cannot express: it
        /// enables every instrument the meter publishes, so no test written over it can show one instrument
        /// staying silent while its sibling records.
        /// </summary>
        /// <remarks>
        /// Disposal disables the instrument it enabled, because disposing the .NET listener alone leaves the
        /// instrument <see cref="Instrument.Enabled"/> for every later test in the process.
        /// </remarks>
        private sealed class SingleInstrumentRecordingScope : IDisposable
        {
            private readonly string _meterName;
            private readonly string _instrumentName;
            private readonly MeterListener _netMeterListener;
            private readonly List<RecordedMeasurement> _measurements = new List<RecordedMeasurement>();
            private readonly object _sync = new object();
            private Instrument _enabledInstrument;
            private bool _disposed;

            public SingleInstrumentRecordingScope(string meterName, string instrumentName)
            {
                _meterName = meterName;
                _instrumentName = instrumentName;

                _netMeterListener = new MeterListener
                {
                    InstrumentPublished = EnableWhenNameMatches,
                };

                _netMeterListener.SetMeasurementEventCallback<long>(RecordLong);
                _netMeterListener.Start();
            }

            /// <summary>The recorded measurements whose <see cref="Instrument.Name"/> matches exactly.</summary>
            public IReadOnlyList<RecordedMeasurement> MeasurementsFor(string instrumentName)
            {
                lock (_sync)
                {
                    return _measurements.FindAll(measurement => measurement.InstrumentName == instrumentName);
                }
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

            private void RecordLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
            {
                var recordedTags = tags.ToArray();

                lock (_sync)
                {
                    _measurements.Add(new RecordedMeasurement(instrument.Meter.Name, instrument.Name, value, recordedTags));
                }
            }
        }
    }
}
