using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityDiagnostics
{
    /// <summary>
    /// The DOCUMENT-TIER drain observed as its own send hop: the Outbox Document is published to broker
    /// infrastructure long after the batch that wrote it, from a change-feed processor in another process, where it
    /// can fail entirely on its own.
    /// </summary>
    /// <remarks>
    /// THE LOAD-BEARING ASSERTION is the parenting one. The trace is NOT severed at this hop today — the
    /// <c>traceparent</c> is injected at message construction, persisted inside the document's MessageContext and
    /// materialized on drain — so the only thing missing was observation. Severing is the hazard that ADDING
    /// observation could introduce: a drain span parented off <see cref="Activity.Current"/> would adopt the CHANGE
    /// FEED's ambient activity and then overwrite the persisted write-time record with it, reporting that the feed
    /// caused the message when the write did (ADR-0010 D6). Every parenting test below therefore runs with a
    /// deliberately-different ambient activity attached, so a regression to ambient parenting fails rather than
    /// passes quietly.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenDrainingWithDiagnosticsEnabled : Testing.Core.Context
    {
        // The W3C Trace Context specification's own example traceparent, standing in for the value the Outbox
        // Document persisted at write time.
        private const string PersistedTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        private const string PersistedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        private const string PersistedSpanId = "00f067aa0ba902b7";
        private const string InfrastructureType = "test-infra";
        private const string Destination = "orders";
        private const string TenantId = "tenant-1";

        /// <summary>How long the drained Outbox Document has been pending, in the lag assertions below.</summary>
        private const int PendingAgeSeconds = 300;

        /// <summary>A drain publishes exactly one document, so the batch count on its send span is always one.</summary>
        private const int DrainedMessageCount = 1;

        /// <summary>
        /// The name of the drain's diagnostics wrapper, matched against a failed publish's async stack trace to pin
        /// that the opted-out drain never enters it (ADR-0010 R1/R4).
        /// </summary>
        /// <remarks>
        /// DELIBERATE COUPLING to a private method name. The property under test is the SHAPE of the opted-out call
        /// path, and shape is not observable through behaviour here: the off path and the on path publish the same
        /// bytes to the same dispatcher. Renaming the wrapper without updating this constant turns the guard test
        /// vacuous, which is why the positive control beside it asserts the frame IS present when diagnostics are on.
        /// </remarks>
        private const string DiagnosticsWrapperName = "DispatchObserved";

        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        [Fact]
        public async Task MustParentTheDrainSpanToThePersistedWriteTimeTraceContext()
        {
            // THE FALSE-CAUSALITY REGRESSION GUARD. The ambient activity here is the change feed's; the drain span
            // must be a child of the context the WRITER persisted and never of the activity the feed happens to be
            // running under.
            var (provider, _) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                drainSpan.TraceId.ToHexString().Should().Be(PersistedTraceId);
                drainSpan.ParentSpanId.ToHexString().Should().Be(PersistedSpanId);
                drainSpan.Parent.Should().BeNull("the causal parent is a persisted trace context, not a running Activity");
                drainSpan.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId,
                    "adopting the change feed's ambient trace would report that the feed caused the message when the write did");
                drainSpan.Links.Should().ContainSingle().Which.Context.Should().Be(foreignInstrumentation.ForeignActivity.Context,
                    "the ambient rides along as a LINK, never promoted to parent (ADR-0010 D6)");
            }
        }

        [Fact]
        public async Task MustStartAFreshRootWhenTheDrainedDocumentCarriesNoPersistedTraceContext()
        {
            // A document written while diagnostics were off carries no traceparent. Absence must stay absence:
            // falling back to the ambient here is the same false causality the test above rejects, and it is worse,
            // because a fresh root is at least honest about having no known parent.
            var (provider, _) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(persistedTraceParent: null), container.Object, PartitionKeyPath);

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                drainSpan.Parent.Should().BeNull();
                drainSpan.ParentSpanId.Should().Be(default(ActivitySpanId));
                drainSpan.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId);
                drainSpan.Links.Should().ContainSingle().Which.Context.Should().Be(foreignInstrumentation.ForeignActivity.Context);
            }
        }

        [Fact]
        public async Task MustEmitASendSpanAndASentMessagesMeasurementForADrainedDocument()
        {
            var (provider, _) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                drainSpan.Kind.Should().Be(ActivityKind.Producer);
                drainSpan.DisplayName.Should().Be(BrokerDiagnostics.OperationTypes.Send + " " + Destination);
                drainSpan.GetTagItem(BrokerDiagnostics.MessagingSystem).Should().Be(InfrastructureType);
                drainSpan.GetTagItem(BrokerDiagnostics.DestinationName).Should().Be(Destination);
                drainSpan.GetTagItem(BrokerDiagnostics.OperationType).Should().Be(BrokerDiagnostics.OperationTypes.Send);
                drainSpan.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(DrainedMessageCount);

                var sent = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
                sent.Value.Should().Be(DrainedMessageCount);
                sent.TryGetTag(BrokerDiagnostics.MessagingSystem, out var messagingSystem).Should().BeTrue();
                messagingSystem.Should().Be(InfrastructureType);
                sent.TryGetTag(BrokerDiagnostics.DestinationName, out var destination).Should().BeTrue();
                destination.Should().Be(Destination);
            }
        }

        [Fact]
        public async Task MustRecordTheDurationOfTheDrainHop()
        {
            // The whole point of observing this hop: how long the publish took, long after the write, is not
            // derivable from anything the write-time span recorded.
            var (provider, _) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;
                duration.Value.Should().BeGreaterThanOrEqualTo(0);
                duration.TryGetTag(BrokerDiagnostics.OperationType, out var operationType).Should().BeTrue();
                operationType.Should().Be(BrokerDiagnostics.OperationTypes.Send);
                duration.TryGetTag(BrokerDiagnostics.DestinationName, out var destination).Should().BeTrue();
                destination.Should().Be(Destination);
            }
        }

        [Fact]
        public async Task MustWriteTheDrainSpanTraceContextOntoTheDispatchedMessage()
        {
            // The drain span is the hop that actually put the message on the broker, so it is what a downstream
            // receive must parent to. It carries the write-time trace id, so the chain stays contiguous.
            var (provider, published) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                OutboundBrokeredMessage dispatched = published.Should().ContainSingle().Subject;
                dispatched.MessageContext[TraceContextHeaders.TraceParent].Should().Be(drainSpan.Id);
                drainSpan.TraceId.ToHexString().Should().Be(PersistedTraceId);
            }
        }

        [Fact]
        public async Task MustLeaveThePersistedTraceContextOnTheWireWhenTheDrainSpanIsSampledOut()
        {
            // ADR-0010 D9 on the DEFERRED path: Chatter tracing is opted into but the .NET ActivityListener declined
            // to sample, so there is no drain span to travel. The fallback resolves to the PERSISTED context — which
            // is already on the wire — never to Activity.Current, whose value here is the change feed's.
            var (provider, published) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (new ForeignInstrumentationScope())
            using (var sampledOut = new SampledOutActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                sampledOut.StartedActivities.Should().BeEmpty();
                OutboundBrokeredMessage dispatched = published.Should().ContainSingle().Subject;
                dispatched.MessageContext[TraceContextHeaders.TraceParent].Should().Be(PersistedTraceParent,
                    "a sampled-out deferred send injects nothing, so the persisted record rides out unchanged");
            }
        }

        [Fact]
        public async Task MustRecordAPublishFailureOnTheDrainSpanAndLeaveTheDocumentPending()
        {
            var (container, patches) = RecordingContainer();
            var failure = new DrainProbeException("the broker publish failed deliberately");

            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                CosmosOutboxRelay relay = Relay(ThrowingProvider(failure));

                Func<Task> act = () => relay.ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                await act.Should().ThrowAsync<DrainProbeException>("observing the drain does not change how it handles a failed publish");

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                drainSpan.Status.Should().Be(ActivityStatusCode.Error);
                drainSpan.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(DrainProbeException).FullName);

                var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;
                duration.TryGetTag(ChatterTelemetryTags.ErrorType, out var errorType).Should().BeTrue();
                errorType.Should().Be(typeof(DrainProbeException).FullName);

                patches.Should().BeEmpty("a failed publish stamps no delivered/TTL, so the document re-surfaces (at-least-once)");
            }
        }

        [Fact]
        public async Task MustHandTheAmbientActivityBackAfterTheDrain()
        {
            var (provider, _) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        [Fact]
        public async Task MustLeaveThePersistedTraceContextOnTheWireWhileBrokerDiagnosticsAreOff()
        {
            // An application that never opted in gets no wire write from the drain and no ambient activity taken
            // away from it (ADR-0010 R1, R2).
            var (provider, published) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                BrokerDiagnostics.IsEnabled.Should().BeFalse();

                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                OutboundBrokeredMessage dispatched = published.Should().ContainSingle("the drain still publishes when nothing is listening").Subject;
                dispatched.MessageContext[TraceContextHeaders.TraceParent].Should().Be(PersistedTraceParent);
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        [Fact]
        public async Task MustNotEnterTheDiagnosticsWrapperOnTheDrainPathWhileBrokerDiagnosticsAreOff()
        {
            // ADR-0010 R1/R4, pinned STRUCTURALLY because it is not observable any other way: opted out and opted in
            // publish the same bytes to the same dispatcher, so only the SHAPE of the call distinguishes them.
            // Argument evaluation precedes the guard INSIDE SendScope.Open, so the call site is the only place that
            // can decide. A failed publish's async stack trace is what exposes the difference: the wrapper is an
            // async method, so it carries its own frame exactly when the opted-out path went through it.
            var (container, _) = RecordingContainer();

            using (new ForeignInstrumentationScope())
            {
                BrokerDiagnostics.IsEnabled.Should().BeFalse();

                CosmosOutboxRelay relay = Relay(ThrowingProvider(new DrainProbeException("broker unreachable")));

                Func<Task> act = () => relay.ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                var thrown = (await act.Should().ThrowAsync<DrainProbeException>()).Which;
                thrown.StackTrace.Should().NotContain(DiagnosticsWrapperName,
                    "an application that never opted into broker diagnostics must not enter the diagnostics wrapper at all");
            }
        }

        [Fact]
        public async Task MustEnterTheDiagnosticsWrapperOnTheDrainPathWhenBrokerDiagnosticsAreOn()
        {
            // The POSITIVE CONTROL for the guard test above, without which that test would pass just as well against
            // a drain that had no diagnostics wrapper to enter — and would keep passing if the wrapper were renamed.
            var (container, _) = RecordingContainer();

            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                BrokerDiagnostics.IsEnabled.Should().BeTrue();

                CosmosOutboxRelay relay = Relay(ThrowingProvider(new DrainProbeException("broker unreachable")));

                Func<Task> act = () => relay.ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                var thrown = (await act.Should().ThrowAsync<DrainProbeException>()).Which;
                thrown.StackTrace.Should().Contain(DiagnosticsWrapperName,
                    "the opted-in drain is observed by the wrapper, which is exactly what the opted-out path skips");
            }
        }

        [Fact]
        public async Task MustReportEachAtLeastOnceReplayAsItsOwnDrainUnderTheSharedWriteTimeRoot()
        {
            // AT-LEAST-ONCE: a document whose delivered/TTL stamp failed re-surfaces and is published again. Each
            // replay is a distinct hop and reports as one — its own send span and its own count — while every one of
            // them parents to the SAME persisted write-time context, so the replays sit side by side under the write
            // that caused them rather than dissolving into one span or into the change feed's trace.
            var (provider, published) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosOutboxRelay relay = Relay(provider);
                JsonElement document = PendingOutboxDocument(timestampUnixSeconds: SecondsAgo(PendingAgeSeconds));

                await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);
                await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

                published.Should().HaveCount(2);
                activityScope.StoppedActivities.Should().HaveCount(2);
                activityScope.StoppedActivities.Should().OnlyContain(span => span.ParentSpanId.ToHexString() == PersistedSpanId);
                activityScope.StoppedActivities.Select(span => span.SpanId).Should().OnlyHaveUniqueItems(
                    "each replay is its own hop");

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().HaveCount(2,
                    "the counter increments once per replay, which is the honest report");
            }
        }

        [Fact]
        public async Task MustCountASkippedDocumentTheRelayDidNotAdmit()
        {
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                await Relay(provider).ProcessChangeAsync(NonOutboxDocument(), container.Object, PartitionKeyPath);

                published.Should().BeEmpty();
                patches.Should().BeEmpty();

                var counted = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().ContainSingle().Subject;
                counted.Value.Should().Be(1);
                counted.TryGetTag(CosmosReliabilityDiagnostics.DrainOutcome, out var outcome).Should().BeTrue();
                outcome.Should().Be(CosmosReliabilityDiagnostics.DrainOutcomes.Skipped);

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().BeEmpty(
                    "a document the relay never admitted has no admission lag");
            }
        }

        [Fact]
        public async Task MustRecordTheAdmissionLagAndCountAnAdmittedDocument()
        {
            var (provider, published) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                JsonElement document = PendingOutboxDocument(timestampUnixSeconds: SecondsAgo(PendingAgeSeconds));

                await Relay(provider).ProcessChangeAsync(document, container.Object, PartitionKeyPath);

                published.Should().ContainSingle();

                var lag = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().ContainSingle().Subject;
                lag.Value.Should().BeInRange(PendingAgeSeconds - 1, PendingAgeSeconds + 30,
                    "the RAW Cosmos _ts is handed over and the age in SECONDS is derived by the diagnostics surface");

                var counted = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().ContainSingle().Subject;
                counted.TryGetTag(CosmosReliabilityDiagnostics.DrainOutcome, out var outcome).Should().BeTrue();
                outcome.Should().Be(CosmosReliabilityDiagnostics.DrainOutcomes.Admitted);
            }
        }

        [Fact]
        public async Task MustCountADroppedDocumentWhenTheResolverResolvesToNoBrokeredMessage()
        {
            // A null resolution is an intentional drop-and-acknowledge: the document is stamped delivered without a
            // publish. Counting it as dropped is what keeps that drop visible rather than silent.
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                JsonElement document = PendingOutboxDocument(timestampUnixSeconds: SecondsAgo(PendingAgeSeconds));

                await Relay(provider).ProcessChangeAsync(document, container.Object, PartitionKeyPath, NullResolvingResolver());

                published.Should().BeEmpty();
                patches.Should().ContainSingle("a dropped document is still stamped delivered");

                var counted = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().ContainSingle().Subject;
                counted.TryGetTag(CosmosReliabilityDiagnostics.DrainOutcome, out var outcome).Should().BeTrue();
                outcome.Should().Be(CosmosReliabilityDiagnostics.DrainOutcomes.Dropped);
            }
        }

        [Fact]
        public async Task MustRecordTheAdmissionLagButCountNoDocumentWhenThePublishFails()
        {
            // The lag is recorded at ADMISSION, so a publish that throws still reports how long the document had
            // been pending — while contributing nothing to the outcome counter, because it resolved to no outcome.
            var (container, patches) = RecordingContainer();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosOutboxRelay relay = Relay(ThrowingProvider(new DrainProbeException("the broker publish failed deliberately")));
                JsonElement document = PendingOutboxDocument(timestampUnixSeconds: SecondsAgo(PendingAgeSeconds));

                Func<Task> act = () => relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

                await act.Should().ThrowAsync<DrainProbeException>();

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().ContainSingle();
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().BeEmpty(
                    "a document whose publish threw was never resolved, so it is counted under no outcome");
                patches.Should().BeEmpty();
            }
        }

        [Fact]
        public async Task MustCountTheOutcomeWithoutALagWhenTheDocumentCarriesNoCosmosTimestamp()
        {
            // _ts is stamped server-side, so a hand-built document can lack it. An absent timestamp costs the lag
            // measurement only — the outcome is still counted.
            var (provider, _) = RecordingProvider();
            var (container, _) = RecordingContainer();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                await Relay(provider).ProcessChangeAsync(PendingOutboxDocument(), container.Object, PartitionKeyPath);

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().BeEmpty();

                var counted = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().ContainSingle().Subject;
                counted.TryGetTag(CosmosReliabilityDiagnostics.DrainOutcome, out var outcome).Should().BeTrue();
                outcome.Should().Be(CosmosReliabilityDiagnostics.DrainOutcomes.Admitted);
            }
        }

        [Fact]
        public async Task MustCountASkippedDocumentExactlyOnceWhenTheStandaloneHostPreGateRejectsIt()
        {
            // EXACTLY-ONCE COUNTING across the two layers: the standalone host's pure-identity pre-gate rejects a
            // co-resident non-outbox write, so the document is counted there and never reaches the relay's own
            // admission gate to be counted a second time.
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            using (Stream batch = BatchOf(NonOutboxDocument()))
            {
                await StandaloneHost(provider).HandleChangesAsync(batch, container.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

                published.Should().BeEmpty();
                patches.Should().BeEmpty();

                var counted = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().ContainSingle(
                    "a non-pending document is counted once at the host pre-gate and never reaches the relay").Subject;
                counted.TryGetTag(CosmosReliabilityDiagnostics.DrainOutcome, out var outcome).Should().BeTrue();
                outcome.Should().Be(CosmosReliabilityDiagnostics.DrainOutcomes.Skipped);
            }
        }

        [Fact]
        public async Task MustCountAnAdmittedDocumentExactlyOnceOnTheStandaloneHostPath()
        {
            // The other half of exactly-once: a document that clears the pre-gate re-clears the SAME id-guard inside
            // the relay's admission gate, so it is counted once as admitted and never also as skipped.
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            using (Stream batch = BatchOf(PendingOutboxDocument(timestampUnixSeconds: SecondsAgo(PendingAgeSeconds))))
            {
                await StandaloneHost(provider).HandleChangesAsync(batch, container.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

                published.Should().ContainSingle();
                patches.Should().ContainSingle();

                var counted = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().ContainSingle().Subject;
                counted.TryGetTag(CosmosReliabilityDiagnostics.DrainOutcome, out var outcome).Should().BeTrue();
                outcome.Should().Be(CosmosReliabilityDiagnostics.DrainOutcomes.Admitted);
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().ContainSingle();
            }
        }

        // The failure a deliberately-failed publish carries, so the error-type assertions name a type that exists
        // for no other reason.
        private sealed class DrainProbeException : Exception
        {
            public DrainProbeException(string message)
                : base(message)
            {
            }
        }

        // Builds the exact Outbox Document the Document-Tier Batch-Lifecycle Behavior writes, as a JsonElement the
        // relay reads. The MessageContext is serialized through ChatterJson.Options (EF parity), so the write-time
        // traceparent the writer injected round-trips through MaterializePersistedContext exactly as it does in
        // production.
        private static JsonElement PendingOutboxDocument(string messageId = "msg-1", string persistedTraceParent = PersistedTraceParent, long? timestampUnixSeconds = null)
        {
            var converter = new JsonBodyConverter();
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };

            if (persistedTraceParent != null)
            {
                messageContext[TraceContextHeaders.TraceParent] = persistedTraceParent;
            }

            var outbound = new OutboundBrokeredMessage(messageId, new { OrderId = 7 }, messageContext, Destination, converter);
            JsonObject rendered = CosmosOutboxDocument.From(outbound).ToJsonObject(PartitionKeyPath, new List<JsonElement> { JsonValue(TenantId) });

            // Cosmos stamps _ts server-side, so it is added HERE rather than by the document type: a hand-built
            // document that never went through Cosmos carries none, which is its own asserted case below.
            if (timestampUnixSeconds.HasValue)
            {
                rendered[CosmosOutboxDocument.TimestampField] = timestampUnixSeconds.Value;
            }

            return Parse(rendered.ToJsonString());
        }

        // A co-resident document the relay never admits: no Chatter discriminator, no pending status.
        private static JsonElement NonOutboxDocument() => Parse("{\"id\":\"an-aggregate\",\"tenantId\":\"" + TenantId + "\"}");

        private static long SecondsAgo(int seconds) => DateTimeOffset.UtcNow.AddSeconds(-seconds).ToUnixTimeSeconds();

        // A change-feed batch payload in the wire shape the SDK hands the host.
        private static Stream BatchOf(params JsonElement[] documents)
            => new MemoryStream(Encoding.UTF8.GetBytes("{\"Documents\":[" + string.Join(",", documents.Select(d => d.GetRawText())) + "]}"));

        private static StandaloneCosmosOutboxRelayHostedService StandaloneHost(IMessagingInfrastructureProvider provider)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                provider,
                BodyConverterFactory(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => Mock.Of<Container>(),
                    LeaseContainerFactory = _ => Mock.Of<Container>(),
                    PartitionKeyPath = PartitionKeyPath,
                });

        // A resolver whose ResolveAsync returns a completed null-result Task: the admitted document is stamped
        // delivered and dispatches nothing (the intentional drop-and-acknowledge).
        private static IOutboxBodyResolver NullResolvingResolver()
        {
            var resolver = new Mock<IOutboxBodyResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((OutboundBrokeredMessage)null);
            return resolver.Object;
        }

        private static JsonElement JsonValue(string raw) => Parse(JsonSerializer.Serialize(raw));

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static CosmosOutboxRelay Relay(IMessagingInfrastructureProvider provider)
            => new CosmosOutboxRelay(provider, BodyConverterFactory());

        private static IBodyConverterFactory BodyConverterFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        // A provider whose dispatcher records every dispatched message; the recorded list is the publish ledger, and
        // the recorded message's context is the only place the wire write is observable.
        private static (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published) RecordingProvider()
        {
            var published = new List<OutboundBrokeredMessage>();
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => published.Add(m))
                      .Returns(Task.CompletedTask);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return (provider.Object, published);
        }

        // A provider whose dispatcher throws on publish — the publish-failure path.
        private static IMessagingInfrastructureProvider ThrowingProvider(Exception toThrow)
        {
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .ThrowsAsync(toThrow);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return provider.Object;
        }

        // A container that records each PatchItemAsync call and returns a benign response, so the delivered/TTL
        // stamp is observable without a live SDK.
        private static (Mock<Container> container, List<string> patches) RecordingContainer()
        {
            var patches = new List<string>();
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Callback<string, PartitionKey, IReadOnlyList<PatchOperation>, PatchItemRequestOptions, CancellationToken>(
                        (id, _, __, ___, ____) => patches.Add(id))
                     .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            return (container, patches);
        }
    }
}
