using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The receive path with a .NET <c>MeterListener</c> and NO .NET <c>ActivityListener</c>: the metrics half of
    /// the surface must be truthful on its own, because such an application has no span to read a failure off.
    /// </summary>
    /// <remarks>
    /// The worker's error ladder settles an expected fault — a poisoned message, an exhausted delivery, a nack —
    /// and then returns NORMALLY, so the worker's success path cannot observe it. Reporting those deliveries
    /// without <c>error.type</c> would show a failed receive as a successful operation, which is what semconv
    /// v1.30.0 requires the attribute to prevent (ADR-0010 D4).
    /// The fault is retained ONCE, at the worker's exception-filter choke point, so these cases pin the choke
    /// point rather than each ladder branch's own bookkeeping (ADR-0010 D11).
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenReceivingWithMetricsOnly : Testing.Core.Context
    {
        [Fact]
        public async Task MustTagTheReceiveMetricsWithTheErrorTypeOfAPoisonedDeliveryTheLadderDeadlettered()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(new Dictionary<string, object>(), body: new JsonBodyConverter().GetBytes("not-valid-json-object"));

                await harness.RunUntilSettledAsync(ReceiverCall.Deadletter);

                AssertErrorType(meterScope, typeof(PoisonedMessageException).FullName);
            }
        }

        [Fact]
        public async Task MustTagTheReceiveMetricsWithTheErrorTypeOfADeliveryTheLadderNacked()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness(failedDispatchCount: int.MaxValue, deliveryCount: 1, maxReceiveAttempts: 10))
            {
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Nack);

                AssertErrorType(meterScope, typeof(DiagnosticsProbeException).FullName);
            }
        }

        /// <summary>
        /// The generic ladder branch that deadletters an EXHAUSTED delivery reaches its settlement only after the
        /// delivery-count probe returns, so it is a different branch from the poisoned-message deadletter above.
        /// </summary>
        [Fact]
        public async Task MustTagTheReceiveMetricsWithTheErrorTypeOfADeliveryTheLadderDeadletteredForExceedingTheMaximum()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness(failedDispatchCount: int.MaxValue, deliveryCount: 5, maxReceiveAttempts: 1))
            {
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Deadletter);

                AssertErrorType(meterScope, typeof(DiagnosticsProbeException).FullName);
            }
        }

        /// <summary>
        /// A cancellation raised while the receiver is still running is a GENUINE failed receive, and is reported
        /// as one. Only a cancellation observed while the worker's own token is cancelled — the receiver being torn
        /// down underneath an in-flight delivery — is exempt, so a clean shutdown does not emit a burst of failed
        /// receives (ADR-0010 D11). This pins the running half of that decision boundary.
        /// </summary>
        [Fact]
        public async Task MustTagTheReceiveMetricsWithTheErrorTypeOfACancellationRaisedWhileTheReceiverIsStillRunning()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness(deliveryCount: 1, maxReceiveAttempts: 10))
            {
                harness.OnDispatch = _ => throw new OperationCanceledException("The handler cancelled its own work.");
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Nack);

                AssertErrorType(meterScope, typeof(OperationCanceledException).FullName);
            }
        }

        /// <summary>
        /// The settle path is the ONE place a delivery's fault is swallowed into a <c>bool</c> instead of leaving the
        /// worker's processing block, so the exception-filter choke point cannot observe it. A metrics-only
        /// application has no span to read the failure off, which makes this the case the metric must carry
        /// (ADR-0010 D4, D11).
        /// </summary>
        [Fact]
        public async Task MustTagTheReceiveMetricsWithTheErrorTypeOfAnAcknowledgementThatFailed()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.ArmAckFailure(new DiagnosticsProbeException("The acknowledgment failed deliberately."));
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                AssertErrorType(meterScope, typeof(DiagnosticsProbeException).FullName);
            }
        }

        [Fact]
        public async Task MustLeaveTheReceiveMetricsUntaggedWhenTheDeliverySucceeds()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                AssertErrorType(meterScope, expectedErrorType: null);
            }
        }

        /// <summary>
        /// Asserts both receive instruments recorded exactly one measurement carrying
        /// <paramref name="expectedErrorType"/>, or carrying no <c>error.type</c> at all when it is <c>null</c>.
        /// </summary>
        private static void AssertErrorType(RecordingMeterScope meterScope, string expectedErrorType)
        {
            var consumed = meterScope.MeasurementsFor(BrokerDiagnostics.ConsumedMessagesInstrumentName).Should().ContainSingle().Subject;
            var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;

            consumed.Value.Should().Be(1);
            consumed.TryGetTag(BrokerDiagnostics.OperationType, out var operationType).Should().BeTrue();
            operationType.Should().Be(BrokerDiagnostics.OperationTypes.Receive);

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
