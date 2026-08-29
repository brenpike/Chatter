using Chatter.MessageBrokers.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The three-configuration interoperability matrix — Chatter only, a stand-in broker SDK only, and both — proving
    /// ADR-0010 D8: Chatter neither suppresses nor namespaces against a broker SDK's own instrumentation, trace-id
    /// continuity holds under ambient nesting, and same-key last-writer-wins on <c>traceparent</c> is safe because
    /// both writers derive their context from the same ambient <see cref="Activity"/> chain.
    /// </summary>
    /// <remarks>
    /// TRANSPORT-FREE ON PURPOSE. A second <see cref="ActivitySource"/> stands in for a broker SDK's own
    /// instrumentation so this matrix runs in a plain <c>dotnet test</c>; the matrices against the real Azure Service
    /// Bus and RabbitMQ SDKs live with those adapters.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenAnSdkAlsoInstruments : Testing.Core.Context, IDisposable
    {
        /// <summary>An <see cref="ActivitySource"/> name standing in for a broker SDK's own instrumentation scope.</summary>
        private const string SdkSourceName = "Contoso.Broker.Sdk";

        private readonly ActivitySource _sdkSource = new ActivitySource(SdkSourceName);

        public void Dispose() => _sdkSource.Dispose();

        [Fact]
        public async Task MustEmitOnlyTheChatterSpanWhenOnlyChatterIsInstrumented()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();

                using (var sdkActivity = _sdkSource.StartActivity("sdk.publish"))
                {
                    sdkActivity.Should().BeNull("the stand-in SDK source has no .NET ActivityListener in this configuration");

                    await harness.SendOne();
                }

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.Source.Name.Should().Be(BrokerDiagnostics.ActivitySourceName);
                ResolveTraceParent(harness).Should().StartWith("00-" + span.TraceId.ToHexString() + "-" + span.SpanId.ToHexString());
            }
        }

        [Fact]
        public async Task MustLeaveTheWireUntouchedWhenOnlyTheSdkIsInstrumented()
        {
            using (var activityScope = new RecordingActivityScope(SdkSourceName))
            {
                var harness = new DiagnosticsSendHarness();

                using (var sdkActivity = _sdkSource.StartActivity("sdk.publish"))
                {
                    sdkActivity.Should().NotBeNull();
                    Activity.Current.Should().BeSameAs(sdkActivity);

                    await harness.SendOne();
                }

                BrokerDiagnostics.Source.HasListeners().Should().BeFalse();
                activityScope.StoppedActivities.Should().ContainSingle().Which.Source.Name.Should().Be(SdkSourceName);

                var outboundContext = harness.RoutedMessages.Should().ContainSingle().Subject.MessageContext;
                outboundContext.Should().NotContainKey(TraceContextHeaders.TraceParent);
            }
        }

        [Fact]
        public async Task MustNestTheChatterSpanInsideTheSdkSpanWhenBothInstrument()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName, SdkSourceName))
            {
                var harness = new DiagnosticsSendHarness();

                using (var sdkActivity = _sdkSource.StartActivity("sdk.publish"))
                {
                    await harness.SendOne();

                    // No suppression: both spans exist, and the Chatter span is an ordinary child of the SDK's.
                    var chatterSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                    chatterSpan.Source.Name.Should().Be(BrokerDiagnostics.ActivitySourceName);
                    chatterSpan.TraceId.Should().Be(sdkActivity.TraceId);
                    chatterSpan.ParentSpanId.Should().Be(sdkActivity.SpanId);
                    ResolveTraceParent(harness).Should().StartWith("00-" + sdkActivity.TraceId.ToHexString() + "-" + chatterSpan.SpanId.ToHexString());
                }
            }
        }

        [Fact]
        public async Task MustKeepTraceIdContinuityWhenTheSdkWritesTraceContextLast()
        {
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName, SdkSourceName))
            {
                var harness = new DiagnosticsSendHarness();

                using (var sdkActivity = _sdkSource.StartActivity("sdk.publish"))
                {
                    await harness.SendOne();

                    var outboundContext = harness.RoutedMessages.Should().ContainSingle().Subject.MessageContext;
                    var chatterTraceParent = ResolveTraceParent(harness);

                    // The SDK's own instrumentation writes the SAME key onto the SAME message afterwards, from a span
                    // it derived from the same ambient chain. Last writer wins on the value; the trace id cannot
                    // change, which is the whole reason no key namespacing is needed (ADR-0010 D8).
                    using (var sdkSendActivity = _sdkSource.StartActivity("sdk.send"))
                    {
                        TraceContextPropagator.Inject(sdkSendActivity, outboundContext);

                        var lastWrittenTraceParent = ResolveTraceParent(harness);
                        lastWrittenTraceParent.Should().NotBe(chatterTraceParent);
                        lastWrittenTraceParent.Should().StartWith("00-" + sdkActivity.TraceId.ToHexString() + "-" + sdkSendActivity.SpanId.ToHexString());
                        chatterTraceParent.Should().StartWith("00-" + sdkActivity.TraceId.ToHexString());
                    }
                }
            }
        }

        private static string ResolveTraceParent(DiagnosticsSendHarness harness)
        {
            var outboundContext = harness.RoutedMessages.Should().ContainSingle().Subject.MessageContext;

            outboundContext.TryGetValue(TraceContextHeaders.TraceParent, out var traceParent)
                .Should().BeTrue("the outbound message should carry a '" + TraceContextHeaders.TraceParent + "'");

            return traceParent.Should().BeOfType<string>().Subject;
        }
    }
}
