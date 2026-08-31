using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The send path of a dispatch, forward or reply that names NO infrastructure type: the call is instrumented
    /// exactly as any other, but <c>messaging.system</c> has no value to report and must therefore be left UNSET on
    /// the span rather than reported as an empty string.
    /// </summary>
    /// <remarks>
    /// A blank infrastructure type is a legitimate configuration: it is the Messaging Infrastructure LOOKUP key, and
    /// blank means "the first registered Messaging Infrastructure". It is nonetheless not an attribute VALUE, so a
    /// send span reporting <c>messaging.system=""</c> reports an absence instead of leaving one (issue #293).
    /// All THREE send call sites are covered — dispatch, forward and reply — because the normalization is CENTRAL to
    /// <c>BrokerDiagnostics</c>, and this class is what pins that every one of them reaches it.
    /// The instruments behave differently from the span BY DESIGN: an attribute unset on a span still appears on the
    /// instruments as a key carrying a null value, because a metric's attribute set is fixed per instrument. That is
    /// the module README's existing doctrine.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenSendingWithoutAnInfrastructureType : Testing.Core.Context
    {
        private const string ForwardDestination = "forward-destination";
        private const string ReplyDestination = "reply-destination";
        private const string ReplyGroupId = "reply-group";

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustLeaveTheMessagingSystemUnsetOnTheDispatchSpan(string blankInfrastructureType)
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness(infrastructureType: blankInfrastructureType);

                await harness.SendOne();

                AssertMessagingSystemUnset(activityScope);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustCarryANullMessagingSystemOnTheDispatchMetrics(string blankInfrastructureType)
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var harness = new DiagnosticsSendHarness(infrastructureType: blankInfrastructureType);

                await harness.SendOne();

                AssertMessagingSystem(meterScope, expectedMessagingSystem: null);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustLeaveTheMessagingSystemUnsetOnTheForwardSpan(string blankInfrastructureType)
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await ForwardAsync(blankInfrastructureType);

                AssertMessagingSystemUnset(activityScope);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustCarryANullMessagingSystemOnTheForwardMetrics(string blankInfrastructureType)
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                await ForwardAsync(blankInfrastructureType);

                AssertMessagingSystem(meterScope, expectedMessagingSystem: null);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustLeaveTheMessagingSystemUnsetOnTheReplySpan(string blankInfrastructureType)
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await ReplyAsync(blankInfrastructureType);

                AssertMessagingSystemUnset(activityScope);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustCarryANullMessagingSystemOnTheReplyMetrics(string blankInfrastructureType)
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                await ReplyAsync(blankInfrastructureType);

                AssertMessagingSystem(meterScope, expectedMessagingSystem: null);
            }
        }

        [Fact]
        public async Task MustCarryTheConfiguredMessagingSystemOnTheDispatchMetrics()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var harness = new DiagnosticsSendHarness();

                await harness.SendOne();

                AssertMessagingSystem(meterScope, DiagnosticsSendHarness.MessagingSystem);
            }
        }

        private static Task ForwardAsync(string infrastructureType)
        {
            var routing = new CapturingRoutingHarness();
            var inbound = CapturingRoutingHarness.BuildInbound(traceParent: null, infrastructureType: infrastructureType);

            return new ForwardingRouter(routing.Router, routing.MessageIdGenerator).Route(inbound, ForwardDestination, null);
        }

        private static Task ReplyAsync(string infrastructureType)
        {
            var routing = new CapturingRoutingHarness();
            var inbound = CapturingRoutingHarness.BuildInbound(traceParent: null, infrastructureType: infrastructureType);

            return new ReplyRouter(routing.Router, routing.MessageIdGenerator)
                .Route(inbound, null, new ReplyToRoutingContext(ReplyDestination, ReplyGroupId));
        }

        /// <summary>
        /// Asserts the single stopped span carries NO <c>messaging.system</c> KEY. Asserted as key ABSENCE rather
        /// than a null value: an attribute present with a null value is a reported absence, which is exactly the
        /// shape these tests exist to reject.
        /// </summary>
        private static void AssertMessagingSystemUnset(RecordingActivityScope activityScope)
        {
            var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
            span.TagObjects.Should().NotContain(tag => tag.Key == BrokerDiagnostics.MessagingSystem);
        }

        /// <summary>
        /// Asserts both send instruments recorded exactly one measurement carrying the <c>messaging.system</c> KEY,
        /// whose value is <paramref name="expectedMessagingSystem"/>. The key is asserted PRESENT on both branches:
        /// an instrument's attribute set is fixed, so a blank infrastructure type reports the key with a null value
        /// rather than dropping it.
        /// </summary>
        private static void AssertMessagingSystem(RecordingMeterScope meterScope, string expectedMessagingSystem)
        {
            var sentMessages = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
            var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;

            foreach (var measurement in new[] { sentMessages, duration })
            {
                measurement.TryGetTag(BrokerDiagnostics.MessagingSystem, out var messagingSystem).Should().BeTrue();
                messagingSystem.Should().Be(expectedMessagingSystem);
            }
        }
    }
}
