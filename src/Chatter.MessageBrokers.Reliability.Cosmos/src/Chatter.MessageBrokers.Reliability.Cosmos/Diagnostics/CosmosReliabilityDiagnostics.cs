using Chatter.CQRS.Diagnostics;
using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics
{
    /// <summary>
    /// The opt-in tracing and metrics surface for this module's Outbox Relay. Built on the .NET base class library
    /// only (<see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>), so
    /// a consuming application chooses its own collector without Chatter taking a telemetry dependency.
    /// </summary>
    /// <remarks>
    /// The scope is named for this assembly rather than a flat <c>"Chatter"</c> (ADR-0010 D3): an application opts in
    /// with <c>.AddSource("Chatter.*")</c> / <c>.AddMeter("Chatter.*")</c>, or by naming each scope exactly.
    /// INVARIANT: the off-guard is this module's OWN <see cref="ActivitySource.HasListeners"/> or
    /// <see cref="Instrument.Enabled"/> — never <see cref="Activity.Current"/>, which is non-null in any host that
    /// runs unrelated instrumentation and therefore does not mean Chatter diagnostics are on (ADR-0010 R1, R2).
    /// INVARIANT: an off-guard is the FIRST statement of every emit method, so an application that never opted in
    /// pays one boolean read; no tag value, timestamp or lease token is built before that guard passes. Each method
    /// guards on the SPECIFIC instrument it records and never on <see cref="IsEnabled"/>, because the type-wide
    /// property ORs in <see cref="ActivitySource.HasListeners"/> and would therefore enter the metric path for an
    /// application that opted into TRACING only. <see cref="IsEnabled"/> is the guard for a CALL SITE deciding
    /// whether to do instrumented work at all, not for an emit method deciding whether to publish a measurement.
    /// INVARIANT: this module declares NO send span, here or anywhere else. The shared send path owns the messaging
    /// semantic-convention send span and this module's drain metrics are recorded UNDER it, so a drained Outbox
    /// Document is never reported by two send spans.
    /// The instrument and attribute names are Chatter-native under a <c>chatter.</c> prefix because OpenTelemetry
    /// messaging semantic conventions v1.30.0 covers no outbox drain concept; inventing a <c>messaging.*</c> spelling
    /// for one would be a false claim of conformance (ADR-0010 D4). The one exception is
    /// <see cref="ChatterTelemetryTags.ErrorType"/>: <c>error.type</c> is a general-purpose registry attribute
    /// defined OUTSIDE messaging semconv, so it is emitted under its standard spelling, through the shared constant
    /// every other Chatter emit site uses, rather than under a second Chatter-native name for one concept.
    /// </remarks>
    public static class CosmosReliabilityDiagnostics
    {
        /// <summary>The name of the <see cref="ActivitySource"/> an application subscribes to for this module's spans.</summary>
        /// <remarks>
        /// The scope carries no span today, and the module deliberately emits none. The name is declared and included
        /// in <see cref="IsEnabled"/> so the per-assembly scope is reserved under ADR-0010 D3: a later module-native
        /// span joins the same scope an application already subscribes to, with no rename and no guard change.
        /// </remarks>
        public const string ActivitySourceName = "Chatter.MessageBrokers.Reliability.Cosmos";

        /// <summary>The name of the <see cref="Meter"/> an application subscribes to for this module's instruments.</summary>
        public const string MeterName = "Chatter.MessageBrokers.Reliability.Cosmos";

        /// <summary>The age of an Outbox Document when the Outbox Relay admitted it, recorded in seconds.</summary>
        public const string DrainLagInstrumentName = "chatter.messaging.outbox.drain.lag";

        /// <summary>The number of Outbox Documents the Outbox Relay resolved, by <see cref="DrainOutcome"/>.</summary>
        public const string DrainedDocumentsInstrumentName = "chatter.messaging.outbox.drain.documents";

        /// <summary>The number of documents in one change-feed batch handed to the Outbox Relay.</summary>
        public const string DrainBatchSizeInstrumentName = "chatter.messaging.outbox.drain.batch.size";

        /// <summary>The number of change-feed batches the Outbox Relay handled, by <see cref="LeaseToken"/>.</summary>
        public const string DrainedBatchesInstrumentName = "chatter.messaging.outbox.drain.batches";

        /// <summary>The number of drain attempts that faulted, by <see cref="LeaseToken"/> and error type.</summary>
        public const string DrainFailuresInstrumentName = "chatter.messaging.outbox.drain.failures";

        /// <summary>The number of Outbox Documents the Outbox Relay marked undeliverable, carried with NO attribute.</summary>
        public const string DrainUndeliverableInstrumentName = "chatter.messaging.outbox.drain.undeliverable";

        /// <summary>The number of times the Outbox Relay suspended draining, by <see cref="LeaseToken"/>.</summary>
        public const string DrainSuspensionsInstrumentName = "chatter.messaging.outbox.drain.suspensions";

        /// <summary>How the Outbox Relay resolved one document; values come from <see cref="DrainOutcomes"/>.</summary>
        public const string DrainOutcome = "chatter.messaging.outbox.drain.outcome";

        /// <summary>The change-feed lease the batch was delivered for; the partition-progress dimension.</summary>
        public const string LeaseToken = "chatter.messaging.outbox.lease_token";

        private static readonly string _telemetryVersion = ResolveTelemetryVersion();
        private static readonly ActivitySource _source = new ActivitySource(ActivitySourceName, _telemetryVersion);
        private static readonly Meter _meter = new Meter(MeterName, _telemetryVersion);
#if NET9_0_OR_GREATER
        // INVARIANT: each advice field is declared AFTER _meter and BEFORE the histogram it advises, because C# runs
        // static field initializers in TEXTUAL order; declared below its histogram it would still be null when the
        // histogram is created, and the histogram would silently publish no advice.
        // INVARIANT: the boundaries are strictly ascending and distinct. InstrumentAdvice<T> rejects any other
        // ordering by throwing from this static initializer, which surfaces as a TypeInitializationException on the
        // FIRST touch of the off-guard - including on the uninstrumented path, which pays for telemetry it never
        // enabled (ADR-0010 R1).
        // The boundaries are seconds-sized because the instrument's unit is seconds: a collector that receives no
        // advice falls back to millisecond-sized defaults, so every realistic lag lands in the first bucket and every
        // percentile reports the same number (issue #399). They reach further than the send path's own duration
        // histogram because a drain lag is not one client call - a restarted lease or a backlog leaves a document
        // pending for minutes.
        private static readonly InstrumentAdvice<double> _drainLagAdvice = new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = new double[] { 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 600 }
        };
        private static readonly Histogram<double> _drainLag = _meter.CreateHistogram<double>(DrainLagInstrumentName, "s", "Age of an Outbox Document when the Outbox Relay admitted it.", tags: null, advice: _drainLagAdvice);
        // The same unit-sized-boundary rule, on an instrument whose unit is DOCUMENTS: a change-feed batch carries
        // single or double digits of documents far more often than the thousands a collector's defaults are shaped for.
        private static readonly InstrumentAdvice<int> _drainBatchSizeAdvice = new InstrumentAdvice<int>
        {
            HistogramBucketBoundaries = new int[] { 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000 }
        };
        private static readonly Histogram<int> _drainBatchSize = _meter.CreateHistogram<int>(DrainBatchSizeInstrumentName, "{document}", "Number of documents in one change-feed batch handed to the Outbox Relay.", tags: null, advice: _drainBatchSizeAdvice);
#else
        private static readonly Histogram<double> _drainLag = _meter.CreateHistogram<double>(DrainLagInstrumentName, "s", "Age of an Outbox Document when the Outbox Relay admitted it.");
        private static readonly Histogram<int> _drainBatchSize = _meter.CreateHistogram<int>(DrainBatchSizeInstrumentName, "{document}", "Number of documents in one change-feed batch handed to the Outbox Relay.");
#endif
        private static readonly Counter<long> _drainedDocuments = _meter.CreateCounter<long>(DrainedDocumentsInstrumentName, "{document}", "Number of Outbox Documents the Outbox Relay resolved, by outcome.");
        private static readonly Counter<long> _drainedBatches = _meter.CreateCounter<long>(DrainedBatchesInstrumentName, "{batch}", "Number of change-feed batches the Outbox Relay handled, by lease.");
        private static readonly Counter<long> _drainFailures = _meter.CreateCounter<long>(DrainFailuresInstrumentName, "{failure}", "Number of drain attempts that faulted, by lease and error type.");
        private static readonly Counter<long> _drainUndeliverable = _meter.CreateCounter<long>(DrainUndeliverableInstrumentName, "{document}", "Number of Outbox Documents the Outbox Relay marked undeliverable.");
        private static readonly Counter<long> _drainSuspensions = _meter.CreateCounter<long>(DrainSuspensionsInstrumentName, "{suspension}", "Number of times the Outbox Relay suspended draining, by lease.");

        // INVARIANT: the representable bounds are DERIVED from DateTimeOffset rather than hardcoded, so the range
        // guard in RecordDrainLag can never drift from the range the conversion itself accepts. Neither read can
        // throw and neither field is read by another field initializer, so this pair adds no ordering hazard to the
        // textual-order INVARIANT above.
        private static readonly long _minRepresentableUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
        private static readonly long _maxRepresentableUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

        /// <summary>
        /// Whether an application has opted into this module's diagnostics, either by attaching a .NET
        /// <c>ActivityListener</c> to the <see cref="ActivitySourceName"/> scope or by enabling one of the
        /// <see cref="MeterName"/> scope's instruments on a .NET <c>MeterListener</c>. This is the outer guard a call
        /// site checks before reading a timestamp or building a tag; it is an OR across tracing AND metrics, so
        /// enabling only an instrument is enough to take the instrumented path with no .NET <c>ActivityListener</c>
        /// attached.
        /// </summary>
        public static bool IsEnabled => _source.HasListeners() || _drainLag.Enabled || _drainedDocuments.Enabled || _drainBatchSize.Enabled || _drainedBatches.Enabled || _drainFailures.Enabled || _drainUndeliverable.Enabled || _drainSuspensions.Enabled;

        /// <summary>
        /// Counts one Outbox Document the Outbox Relay resolved.
        /// </summary>
        /// <param name="outcome">One of <see cref="DrainOutcomes"/>, carried as <see cref="DrainOutcome"/>.</param>
        /// <remarks>
        /// INVARIANT: the guard is this instrument's OWN <see cref="Instrument.Enabled"/>, never
        /// <see cref="IsEnabled"/>. The type-wide property ORs in <see cref="ActivitySource.HasListeners"/>, so
        /// guarding on it here would enter the metric path for an application that opted into TRACING only
        /// (ADR-0010 R1).
        /// INVARIANT: the tag is built INSIDE the guard. Passing it as an argument to a guarded call would build it
        /// unconditionally, because C# evaluates arguments before the callee's guard runs.
        /// </remarks>
        internal static void RecordDrainedDocument(string outcome)
        {
            if (!_drainedDocuments.Enabled)
            {
                return;
            }

            var tags = new TagList { { DrainOutcome, outcome } };

            _drainedDocuments.Add(1, tags);
        }

        /// <summary>
        /// Records how long an Outbox Document had been pending when the Outbox Relay admitted it, in seconds.
        /// </summary>
        /// <param name="enqueuedUnixSeconds">The document's RAW Cosmos <c>_ts</c>, in Unix epoch seconds.</param>
        /// <remarks>
        /// INVARIANT: the age is derived HERE, from the raw <c>_ts</c>, so clock-skew handling has exactly one owner
        /// and a call site can never record a lag this method did not compute. A document stamped by a node whose
        /// clock runs ahead of this one dates into the future, and a negative lag is not representable — a document
        /// cannot be admitted before it was written — so the skew is clamped to zero.
        /// INVARIANT: the guard is this instrument's OWN <see cref="Instrument.Enabled"/> and it precedes the clock
        /// read, so a tracing-only opt-in never reads a timestamp (ADR-0010 R1).
        /// INVARIANT: an UNREPRESENTABLE timestamp records NOTHING rather than throwing. A change-feed document can
        /// carry any JSON number that fits a 64-bit integer, and <see cref="DateTimeOffset.FromUnixTimeSeconds"/>
        /// throws outside its own narrower range. The Outbox Relay records this lag BEFORE it reconstructs and
        /// publishes, so a throw here would fault the change-feed handler, block the checkpoint and re-surface the
        /// batch forever - a delivery stopped by OPTIONAL telemetry. An out-of-range value is therefore treated
        /// exactly as an ABSENT one, which is what the relay already does for a document carrying no <c>_ts</c>.
        /// </remarks>
        internal static void RecordDrainLag(long enqueuedUnixSeconds)
        {
            if (!_drainLag.Enabled)
            {
                return;
            }

            if (enqueuedUnixSeconds < _minRepresentableUnixSeconds || enqueuedUnixSeconds > _maxRepresentableUnixSeconds)
            {
                return;
            }

            var lagSeconds = (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(enqueuedUnixSeconds)).TotalSeconds;

            _drainLag.Record(lagSeconds < 0 ? 0 : lagSeconds);
        }

        /// <summary>
        /// Records the size of one change-feed batch handed to the Outbox Relay, and counts the batch.
        /// </summary>
        /// <param name="leaseToken">The change-feed lease the batch was delivered for, carried as <see cref="LeaseToken"/>.</param>
        /// <param name="documentCount">How many documents the batch carried.</param>
        /// <remarks>
        /// INVARIANT: both instruments are emitted from ONE method over ONE <see cref="TagList"/>, so a batch cannot
        /// be sized against one lease and counted against another, and the tag set is built once.
        /// INVARIANT: the outer guard passes when EITHER instrument is enabled and each emit is guarded on its own
        /// <see cref="Instrument.Enabled"/>, so enabling one instrument does not publish the other.
        /// </remarks>
        internal static void RecordDrainedBatch(string leaseToken, int documentCount)
        {
            if (!_drainBatchSize.Enabled && !_drainedBatches.Enabled)
            {
                return;
            }

            var tags = new TagList { { LeaseToken, leaseToken } };

            if (_drainBatchSize.Enabled)
            {
                _drainBatchSize.Record(documentCount, tags);
            }

            if (_drainedBatches.Enabled)
            {
                _drainedBatches.Add(1, tags);
            }
        }

        /// <summary>
        /// Counts one drain attempt that faulted.
        /// </summary>
        /// <param name="leaseToken">The change-feed lease the attempt was made under, carried as <see cref="LeaseToken"/>.</param>
        /// <param name="exception">The failure that ended the attempt; its type is carried as <c>error.type</c>.</param>
        /// <remarks>
        /// INVARIANT: a faulted attempt is NOT a fourth <see cref="DrainOutcomes"/> value. The Outbox Relay records
        /// the document's outcome before it publishes, so an attempt that faults afterwards would be counted twice
        /// on one instrument; an attempt and an outcome are separate facts and are counted on separate instruments.
        /// INVARIANT: the guard is this instrument's OWN <see cref="Instrument.Enabled"/>, never
        /// <see cref="IsEnabled"/>, and the tags — including the resolved error type — are built INSIDE it, because
        /// C# evaluates arguments before the callee's guard runs (ADR-0010 R1).
        /// The error type is resolved through <see cref="ActivityOutcome.ResolveErrorType"/> so this module reports
        /// the same <c>error.type</c> value the shared send path reports for the same exception.
        /// </remarks>
        internal static void RecordDrainFailure(string leaseToken, Exception exception)
        {
            if (!_drainFailures.Enabled)
            {
                return;
            }

            var tags = new TagList
            {
                { LeaseToken, leaseToken },
                { ChatterTelemetryTags.ErrorType, ActivityOutcome.ResolveErrorType(exception) },
            };

            _drainFailures.Add(1, tags);
        }

        /// <summary>
        /// Counts one Outbox Document the Outbox Relay marked undeliverable because it violates the Outbox Document
        /// Contract.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this count carries NO attribute, deliberately. One document can violate several contract facts
        /// at once, and a single-valued attribute may not claim one value for a heterogeneous set (ADR-0010 D7) —
        /// naming the first violation would be a false claim about the rest. The full violation text rides the
        /// always-on log instead, which is also the only channel a meter-less application has.
        /// INVARIANT: the guard is this instrument's OWN <see cref="Instrument.Enabled"/>, never
        /// <see cref="IsEnabled"/>, which ORs in <see cref="ActivitySource.HasListeners"/> and would therefore enter
        /// the metric path for an application that opted into TRACING only (ADR-0010 R1).
        /// </remarks>
        internal static void RecordUndeliverableDocument()
        {
            if (!_drainUndeliverable.Enabled)
            {
                return;
            }

            _drainUndeliverable.Add(1);
        }

        /// <summary>
        /// Counts one suspension of the Outbox Relay's drain.
        /// </summary>
        /// <param name="leaseToken">The change-feed lease draining was suspended for, carried as <see cref="LeaseToken"/>.</param>
        /// <remarks>
        /// INVARIANT: a suspension is reported against its lease, which is what keeps a suspended lease
        /// distinguishable from an idle one that simply has nothing pending.
        /// INVARIANT: the guard is this instrument's OWN <see cref="Instrument.Enabled"/>, never
        /// <see cref="IsEnabled"/>, and the tag is built INSIDE it, because C# evaluates arguments before the
        /// callee's guard runs (ADR-0010 R1).
        /// </remarks>
        internal static void RecordDrainSuspension(string leaseToken)
        {
            if (!_drainSuspensions.Enabled)
            {
                return;
            }

            var tags = new TagList { { LeaseToken, leaseToken } };

            _drainSuspensions.Add(1, tags);
        }

        private static string ResolveTelemetryVersion()
        {
            var assembly = typeof(CosmosReliabilityDiagnostics).Assembly;
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrEmpty(informationalVersion))
            {
                return assembly.GetName().Version?.ToString();
            }

            var buildMetadataIndex = informationalVersion.IndexOf('+');
            return buildMetadataIndex < 0 ? informationalVersion : informationalVersion.Substring(0, buildMetadataIndex);
        }

        /// <summary>
        /// The permitted values of the <see cref="DrainOutcome"/> attribute, one per way the Outbox Relay resolves a
        /// document it was handed.
        /// </summary>
        public static class DrainOutcomes
        {
            /// <summary>The document was a pending Outbox Document and its brokered message was published.</summary>
            public const string Admitted = "admitted";

            /// <summary>The document was not a pending Outbox Document, so the Outbox Relay never drained it.</summary>
            public const string Skipped = "skipped";

            /// <summary>The document was admitted and resolved to no brokered message, so it was marked delivered without a publish.</summary>
            public const string Dropped = "dropped";
        }
    }
}
