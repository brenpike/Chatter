using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The SHARED send-side ceremony: the ADR-0010 off-guard, the start timestamp, the span, the sampled-out
    /// propagation fallback, the ambient restore and the stop-time metric, owned in ONE place so that no send site
    /// has to hand-roll them and none can get them wrong.
    /// </summary>
    /// <remarks>
    /// The load-bearing assertions here are the OFF ones and the AMBIENT RESTORE ones. Off must cost nothing in an
    /// application that never opted in (ADR-0010 R1, R4), and a scope must hand the host back the
    /// <see cref="Activity"/> that was current when it opened. A send that becomes a FRESH ROOT is started with
    /// <see cref="Activity.Current"/> cleared, so that <see cref="Activity.Start"/> cannot adopt the ambient
    /// activity as parent (ADR-0010 D6), and stopping it restores that deliberate <c>null</c> — DELETING the host's
    /// ambient activity for the remainder of its async flow unless the scope puts it back.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenOpeningASendScope : Testing.Core.Context
    {
        // The W3C Trace Context specification's own example traceparent, standing in for the value an outbox row
        // persisted at write time.
        private const string PersistedTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        private const string PersistedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        private const string PersistedSpanId = "00f067aa0ba902b7";
        private const string ResolvedDestination = "resolved-destination";
        private const int BatchSize = 3;
        private const int ResolvedMessageCount = 2;

        [Fact]
        public void MustBeAWellFormedNoOpWhenDefaultConstructed()
        {
            // `default(SendScope)` is what an off call site holds. Every member has to be callable on it, because a
            // call site that had to test for off before every call would be back to hand-rolling the ceremony.
            var messageContext = new Dictionary<string, object>();
            var scope = default(SendScope);

            scope.Activity.Should().BeNull();
            scope.TraceContextActivity.Should().BeNull();

            scope.Invoking(offScope =>
            {
                offScope.Inject(messageContext);
                offScope.RecordFailure(new DiagnosticsProbeException("nothing is listening"));
                offScope.RecordResolvedDestination(ResolvedDestination);
                offScope.RecordResolvedMessageCount(ResolvedMessageCount);
                offScope.Dispose();
            }).Should().NotThrow();

            messageContext.Should().BeEmpty("an off scope writes no trace context onto the wire (ADR-0010 R2)");
        }

        [Fact]
        public void MustAllocateNothingForAWholeScopeCycleWhileDiagnosticsAreOff()
        {
            // THE PROOF OF R1/R4. An application that never opted in pays the guard and nothing else - no timestamp,
            // no span, no metric, no closure, for the WHOLE open-use-dispose cycle rather than merely for the open.
            BrokerDiagnostics.IsEnabled.Should().BeFalse();

            var messageContext = new Dictionary<string, object>();
            var failure = new DiagnosticsProbeException("nothing is listening");

            var measurement = GuardCostProbe.Measure(() =>
            {
                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize))
                {
                    scope.Inject(messageContext);
                    scope.RecordFailure(failure);
                    scope.RecordResolvedDestination(ResolvedDestination);
                    scope.RecordResolvedMessageCount(ResolvedMessageCount);
                }
            });

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "the off path allocates nothing at all: " + measurement);
            messageContext.Should().BeEmpty();
        }

        [Fact]
        public void MustAllocateNothingForADeferredScopeCycleWhileDiagnosticsAreOff()
        {
            BrokerDiagnostics.IsEnabled.Should().BeFalse();

            var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);
            var messageContext = new Dictionary<string, object>();

            var measurement = GuardCostProbe.Measure(() =>
            {
                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize, persistedParent))
                {
                    scope.Inject(messageContext);
                }
            });

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "the deferred overload's off path allocates nothing either: " + measurement);
            messageContext.Should().BeEmpty();
        }

        [Fact]
        public void MustLeaveTheAmbientActivityAloneWhileDiagnosticsAreOff()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                BrokerDiagnostics.IsEnabled.Should().BeFalse();

                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize, default(ActivityContext)))
                {
                    scope.Activity.Should().BeNull();
                }

                // The off-guard is the FIRST statement, so nothing below it runs - including any Activity.Current
                // read or write (ADR-0010 R1, R3).
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        [Fact]
        public void MustParentTheSendSpanToTheSuppliedContext()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);

                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize, persistedParent))
                {
                    scope.Activity.Should().NotBeNull();
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.TraceId.ToHexString().Should().Be(PersistedTraceId);
                stopped.ParentSpanId.ToHexString().Should().Be(PersistedSpanId);
                stopped.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(BatchSize);
                stopped.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId);
            }
        }

        [Fact]
        public void MustParentTheSendSpanToTheAmbientActivityWhenNoParentIsSupplied()
        {
            // The immediate send is unchanged: the activity that is genuinely its caller stays its parent.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize))
                {
                    scope.Activity.Should().NotBeNull();
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.Parent.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                stopped.Links.Should().BeEmpty("the ambient IS the parent here, so linking it as well would double-count it");
            }
        }

        [Fact]
        public void MustStartAFreshRootWhenTheDeferredOverloadFoundNoPersistedParent()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                using (SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize, default(ActivityContext)))
                {
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.Parent.Should().BeNull();
                stopped.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId);
                stopped.Links.Should().ContainSingle().Which.Context.Should().Be(foreignInstrumentation.ForeignActivity.Context);
            }
        }

        [Fact]
        public void MustRestoreTheAmbientActivityAfterAContextParentedScopeCloses()
        {
            // The deferred send's OTHER branch. This one does not clear Activity.Current, so today's base class
            // library already puts the ambient back when the span stops; the assertion pins the GUARANTEE a call
            // site relies on rather than the mechanism that currently provides it, so the contract still holds if
            // that mechanism ever changes. MustRestoreTheAmbientActivityAfterARootedScopeCloses is the branch where
            // the scope's own restore is what makes it true.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);

                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize, persistedParent))
                {
                    scope.Activity.Should().NotBeNull();
                    Activity.Current.Should().BeSameAs(scope.Activity, "the send span is current for the duration of the send");
                }

                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        [Fact]
        public void MustRestoreTheAmbientActivityAfterARootedScopeCloses()
        {
            // THE HAZARD, and the branch that proves it: a fresh root is started with Activity.Current CLEARED so
            // the ambient cannot be adopted as its parent, and stopping it restores that deliberate null. Delete the
            // scope's restore and this is the assertion that fails - the host's ambient activity is gone for the
            // remainder of its async flow, which is strictly worse than the missing parent the clear prevented.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize, default(ActivityContext)))
                {
                    scope.Activity.Should().NotBeNull();
                }

                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        [Fact]
        public void MustRestoreTheAmbientActivityAfterAnAmbientParentedScopeCloses()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize))
                {
                    scope.Activity.Should().NotBeNull();
                }

                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        [Fact]
        public void MustInjectTheSendSpanTraceContextOntoTheMessageContext()
        {
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var messageContext = new Dictionary<string, object>();

                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize))
                {
                    scope.TraceContextActivity.Should().BeSameAs(scope.Activity);
                    scope.Inject(messageContext);

                    messageContext.Should().ContainKey(TraceContextHeaders.TraceParent);
                    messageContext[TraceContextHeaders.TraceParent].Should().Be(scope.Activity.Id);
                }
            }
        }

        [Fact]
        public void MustFallBackToTheAmbientTraceContextWhenTheSendSpanIsSampledOut()
        {
            // ADR-0010 D9: head sampling makes the span null while Chatter listeners are still attached, and a
            // downstream hop samples independently - so the trace must not break at this hop.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (new SampledOutActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var messageContext = new Dictionary<string, object>();

                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize))
                {
                    scope.Activity.Should().BeNull();
                    scope.TraceContextActivity.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                    scope.Inject(messageContext);
                }

                messageContext[TraceContextHeaders.TraceParent].Should().Be(foreignInstrumentation.ForeignActivity.Id);
            }
        }

        [Fact]
        public void MustNotFallBackToTheAmbientWhenAParentWasSuppliedExplicitly()
        {
            // The deferred send's causal parent is elsewhere by definition. Falling back to the drain loop's ambient
            // would OVERWRITE the persisted traceparent this hop was handed with a false causality; writing nothing
            // leaves the persisted record on the wire, which is what a downstream hop must see.
            using (new ForeignInstrumentationScope())
            using (new SampledOutActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);
                var messageContext = new Dictionary<string, object>
                {
                    [TraceContextHeaders.TraceParent] = PersistedTraceParent,
                };

                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize, persistedParent))
                {
                    scope.Activity.Should().BeNull();
                    scope.TraceContextActivity.Should().BeNull();
                    scope.Inject(messageContext);
                }

                messageContext[TraceContextHeaders.TraceParent].Should().Be(PersistedTraceParent);
            }
        }

        [Fact]
        public void MustMarkTheSpanFailedAndClassifyTheSendMetric()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var failure = new DiagnosticsProbeException("the send failed deliberately");

                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize))
                {
                    scope.RecordFailure(failure);
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.Status.Should().Be(ActivityStatusCode.Error);
                stopped.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(DiagnosticsProbeException).FullName);

                // A span's status and its metric's error class cannot diverge: the failure is recorded ONCE, on the
                // scope, and the scope carries it into the stop-time measurement.
                var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;
                duration.TryGetTag(ChatterTelemetryTags.ErrorType, out var errorType).Should().BeTrue();
                errorType.Should().Be(typeof(DiagnosticsProbeException).FullName);
            }
        }

        [Fact]
        public void MustReportTheResolvedDestinationOnTheSpanAndTheMetric()
        {
            // The destination of an attribute-routed dispatch is resolved BY the enumeration the router performs, so
            // it is unknown when the span begins (ADR-0010 D7).
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, destinationName: null, messageCount: 0))
                {
                    scope.RecordResolvedDestination(ResolvedDestination);
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.GetTagItem(BrokerDiagnostics.DestinationName).Should().Be(ResolvedDestination);
                stopped.DisplayName.Should().Be(BrokerDiagnostics.OperationTypes.Send + " " + ResolvedDestination);

                var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;
                duration.TryGetTag(BrokerDiagnostics.DestinationName, out var destination).Should().BeTrue();
                destination.Should().Be(ResolvedDestination);
            }
        }

        [Fact]
        public void MustReportTheResolvedMessageCountOnTheSpanAndTheMetric()
        {
            // The batch count is unknown at start for the same reason the destination is: nothing has been
            // enumerated yet.
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, messageCount: 0))
                {
                    scope.RecordResolvedMessageCount(ResolvedMessageCount);
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(ResolvedMessageCount);

                var sent = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
                sent.Value.Should().Be(ResolvedMessageCount);
            }
        }

        [Fact]
        public void MustRecordTheSendMeasurementExactlyOnceAndOnlyAtDispose()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize))
                {
                    scope.RecordResolvedDestination(DiagnosticsSendHarness.DestinationPath);

                    meterScope.Measurements.Should().BeEmpty("the duration is not known until the send has finished");
                }

                meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle();

                var sent = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
                sent.Value.Should().Be(BatchSize);
                sent.TryGetTag(BrokerDiagnostics.MessagingSystem, out var messagingSystem).Should().BeTrue();
                messagingSystem.Should().Be(DiagnosticsSendHarness.MessagingSystem);
                sent.TryGetTag(BrokerDiagnostics.OperationType, out var operationType).Should().BeTrue();
                operationType.Should().Be(BrokerDiagnostics.OperationTypes.Send);
            }
        }

        [Fact]
        public void MustRecordTheSendMeasurementForAnApplicationThatOptedIntoMetricsOnly()
        {
            // BrokerDiagnostics.IsEnabled is an OR across the ActivitySource AND the instruments, so a host carrying
            // only a .NET MeterListener takes the instrumented path with no span in existence.
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

                var messageContext = new Dictionary<string, object>();

                using (var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize))
                {
                    scope.Activity.Should().BeNull();
                    scope.TraceContextActivity.Should().BeNull("no Chatter ActivityListener means no traceparent goes onto the wire (ADR-0010 R2)");
                    scope.Inject(messageContext);
                }

                messageContext.Should().BeEmpty();
                meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle();
            }
        }

        [Fact]
        public void MustRecordTheSendMeasurementOnceEvenWhenAScopeCopyIsDisposedTwice()
        {
            // A struct is copyable, so "disposed exactly once" cannot be left to the discipline of every call site:
            // a second disposal must not double-count the send or stop the span twice.
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var scope = SendScope.Open(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, BatchSize);

                scope.Dispose();
                scope.Dispose();

                meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle();
                meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle();
            }
        }
    }
}
