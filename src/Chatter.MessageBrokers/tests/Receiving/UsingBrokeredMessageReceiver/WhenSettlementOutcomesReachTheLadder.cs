using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Tests.Diagnostics;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Transactions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    /// <summary>
    /// The three-valued settlement contract of <see cref="IMessagingInfrastructureReceiver"/>, pinned at the seam:
    /// every one of <see cref="SettlementOutcome.Settled"/>, <see cref="SettlementOutcome.NotRequired"/> and
    /// <see cref="SettlementOutcome.Failed"/> is driven through the receiver's error ladder and its effect on the
    /// local transaction and on the delivery's telemetry is asserted (ADR-0010 D7's contract-test rule).
    /// </summary>
    /// <remarks>
    /// WHY THIS IS A DIAGNOSTICS-COLLECTION TEST CLASS. A .NET <c>ActivityListener</c> and a .NET
    /// <c>MeterListener</c> are process-global, so a class that attaches either must be serialised onto the same
    /// xunit collection as every other diagnostics test in this assembly or it will be observed by — and will
    /// observe — a concurrently running one.
    /// The distinction this class exists for: a <see cref="SettlementOutcome.Failed"/> the infrastructure RETURNS
    /// is a failed receive that no exception carried, so it owes the metric an <c>error.type</c> but must NOT
    /// stamp a synthetic <c>exception</c> span event; a <see cref="SettlementOutcome.NotRequired"/> is not a
    /// failure at all and must stay silent. Collapsing the two — which the former undeclared <c>bool</c> did — is
    /// exactly what makes a settlement that FAILED indistinguishable from one that was never REQUIRED.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenSettlementOutcomesReachTheLadder : Testing.Core.Context
    {
        private const string DeclinedReason = "the delivery could not be located";
        private const string NothingToSettleReason = "at-most-once delivery settles nothing";

        [Fact]
        public async Task MustReportASettlementTheInfrastructureDeclinedAsAFailedReceive()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.ArmAckOutcome(SettlementResult.Failed(DeclinedReason));
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.Status.Should().Be(ActivityStatusCode.Error);
                span.StatusDescription.Should().Be(DeclinedReason);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(BrokerDiagnostics.ErrorTypes.SettlementFailed);

                // No exception was raised, so no exception event may be stamped: a never-thrown marker exception
                // would attach a synthetic stack trace describing something that never happened.
                span.Events.Should().NotContain(activityEvent => activityEvent.Name == ChatterTelemetryTags.ExceptionEventName);

                // The settlement tag still records what Chatter ANSWERED the delivery with. "We answered ack and
                // the infrastructure declined it" is the truthful pair.
                span.GetTagItem(BrokerDiagnostics.Settlement).Should().Be(BrokerDiagnostics.Settlements.Ack);

                AssertReceiveMetricErrorType(meterScope, BrokerDiagnostics.ErrorTypes.SettlementFailed);
            }
        }

        [Fact]
        public async Task MustNotReportAFailureWhenTheInfrastructureHadNothingToSettle()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.ArmLocalTransaction();
                harness.ArmAckOutcome(SettlementResult.NotRequired(NothingToSettleReason));
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.Status.Should().Be(ActivityStatusCode.Unset);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().BeNull();

                AssertReceiveMetricErrorType(meterScope, expectedErrorType: null);

                // The local transaction is completed only for a SETTLED delivery, which preserves exactly what the
                // former `false` return meant here across the NotRequired/Failed split.
                harness.LocalTransactionStatus.Should().Be(TransactionStatus.Aborted);
            }
        }

        [Fact]
        public async Task MustCompleteTheLocalTransactionOnlyWhenTheInfrastructureSettled()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.ArmLocalTransaction();
                harness.ArmAckOutcome(SettlementResult.Settled());
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                harness.LocalTransactionStatus.Should().Be(TransactionStatus.Committed);
                AssertReceiveMetricErrorType(meterScope, expectedErrorType: null);
            }
        }

        /// <summary>
        /// The contrast that makes the no-exception-event claim above meaningful: a settlement fault that WAS
        /// raised still records the exception event and still reports the exception type as <c>error.type</c>, so
        /// the returned-Failed shape narrows nothing for the exception-carried one.
        /// </summary>
        [Fact]
        public async Task MustStillRecordAnExceptionEventWhenTheSettlementFaultWasRaised()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.ArmAckFailure(new DiagnosticsProbeException("The acknowledgment failed deliberately."));
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(DiagnosticsProbeException).FullName);
                span.Events.Select(activityEvent => activityEvent.Name).Should().Contain(ChatterTelemetryTags.ExceptionEventName);
            }
        }

        /// <summary>
        /// Asserts both receive instruments recorded exactly one measurement carrying
        /// <paramref name="expectedErrorType"/>, or carrying no <c>error.type</c> at all when it is <c>null</c>.
        /// </summary>
        private static void AssertReceiveMetricErrorType(RecordingMeterScope meterScope, string expectedErrorType)
        {
            var consumed = meterScope.MeasurementsFor(BrokerDiagnostics.ConsumedMessagesInstrumentName).Should().ContainSingle().Subject;
            var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;

            foreach (var measurement in new[] { consumed, duration })
            {
                if (expectedErrorType is null)
                {
                    measurement.TryGetTag(ChatterTelemetryTags.ErrorType, out _).Should().BeFalse();
                    continue;
                }

                measurement.TryGetTag(ChatterTelemetryTags.ErrorType, out var errorType).Should().BeTrue();
                errorType.Should().Be(expectedErrorType);
            }
        }
    }
}
