using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The receive path of a Brokered Message Receiver configured WITHOUT an infrastructure type: the delivery is
    /// instrumented exactly as any other, but <c>messaging.system</c> has no value to report and must therefore be
    /// left UNSET on the span rather than reported as an empty string.
    /// </summary>
    /// <remarks>
    /// A blank infrastructure type is a legitimate configuration: it is the Messaging Infrastructure LOOKUP key, and
    /// blank means "the first registered Messaging Infrastructure". It is nonetheless not an attribute VALUE, and the
    /// send path already omits it, so a receive span reporting <c>messaging.system=""</c> would spell one absence two
    /// ways across the two halves of one surface (issue #289).
    /// The instruments behave differently from the span BY DESIGN: an attribute unset on a span still appears on the
    /// instruments as a key carrying a null value, because a metric's attribute set is fixed per instrument. That is
    /// the module README's existing doctrine and it is what the send path already does.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenReceivingWithoutAnInfrastructureType : Testing.Core.Context
    {
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustLeaveTheMessagingSystemUnsetOnTheReceiveSpan(string blankInfrastructureType)
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness(infrastructureType: blankInfrastructureType))
            {
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                // Asserted as KEY ABSENCE rather than a null value: an attribute present with a null value is a
                // reported absence, which is exactly the shape this test exists to reject.
                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.TagObjects.Should().NotContain(tag => tag.Key == BrokerDiagnostics.MessagingSystem);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustCarryANullMessagingSystemOnTheReceiveMetrics(string blankInfrastructureType)
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness(infrastructureType: blankInfrastructureType))
            {
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                AssertMessagingSystem(meterScope, expectedMessagingSystem: null);
            }
        }

        [Fact]
        public async Task MustCarryTheConfiguredMessagingSystemOnTheReceiveMetrics()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                AssertMessagingSystem(meterScope, DiagnosticsReceiveHarness.MessagingSystem);
            }
        }

        /// <summary>
        /// Asserts both receive instruments recorded exactly one measurement carrying the <c>messaging.system</c>
        /// KEY, whose value is <paramref name="expectedMessagingSystem"/>. The key is asserted present on both
        /// branches: an instrument's attribute set is fixed, so a blank infrastructure type reports the key with a
        /// null value rather than dropping it.
        /// </summary>
        private static void AssertMessagingSystem(RecordingMeterScope meterScope, string expectedMessagingSystem)
        {
            var consumed = meterScope.MeasurementsFor(BrokerDiagnostics.ConsumedMessagesInstrumentName).Should().ContainSingle().Subject;
            var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;

            foreach (var measurement in new[] { consumed, duration })
            {
                measurement.TryGetTag(BrokerDiagnostics.MessagingSystem, out var messagingSystem).Should().BeTrue();
                messagingSystem.Should().Be(expectedMessagingSystem);
            }
        }
    }
}
