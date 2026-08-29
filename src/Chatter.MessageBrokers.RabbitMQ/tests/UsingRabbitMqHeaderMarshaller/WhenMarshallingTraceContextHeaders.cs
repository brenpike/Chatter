using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.RabbitMQ;
using FluentAssertions;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.UsingRabbitMqHeaderMarshaller
{
    // Proves the RabbitMQ adapter carries W3C trace context with NO production change to this adapter, which is the
    // whole point of ADR-0010 D5: "traceparent"/"tracestate" are declared on Chatter.MessageBrokers'
    // TraceContextHeaders and NEVER on MessageContext, so RabbitMqHeaderMarshaller's static-constructor completeness
    // gate stays satisfied and the two keys ride as ordinary NON-core application headers.
    //
    // Four arms, all against the marshaller's public static surface (the type is internal static; the test
    // assembly's InternalsVisibleTo reaches it):
    //   1. the completeness gate is satisfied and the keys are non-core;
    //   2. outbound, traceparent survives the value coercion as a string;
    //   3. inbound, the value arrives as the AMQP longstr byte[] a real broker delivers and is STILL extractable;
    //   4. the full inject -> marshal out -> marshal back -> extract round trip preserves the trace id.
    //
    // No live broker and no translator — just the marshalling arms plus Chatter's TraceContextPropagator.
    public class WhenMarshallingTraceContextHeaders : Testing.Core.Context
    {
        // The W3C Trace Context specification's own example traceparent (https://www.w3.org/TR/trace-context/#examples).
        private const string SampleTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        private const string SampleTraceId = "0af7651916cd43dd8448eb211c80319c";
        private const string SampleTraceState = "chatter=rabbitmq";

        private static BasicProperties NewProperties() => new BasicProperties();

        // --- 1. THE LOAD-BEARING ARM: the keys live OUTSIDE MessageContext, so the completeness gate is unmoved ---

        // ADR-0010 D5. RabbitMqHeaderMarshaller's static constructor reflects every public static string field on
        // MessageContext and throws naming any that lacks an explicit HeaderDisposition. Declaring the trace-context
        // keys ON MessageContext would therefore have forced a same-release change to THIS package and raised a
        // TypeInitializationException at the first send or receive for anyone who upgraded Chatter.MessageBrokers
        // without also upgrading Chatter.MessageBrokers.RabbitMQ. This asserts both halves of the escape: the keys are
        // absent from the reflected core-key set, and the marshaller's type initializer still runs clean.
        [Fact]
        public void MustLeaveTheCoreKeyCompletenessGateSatisfied()
        {
            var coreKeys = typeof(MessageContext)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.FieldType == typeof(string))
                .Select(field => (string)field.GetValue(null))
                .ToList();

            coreKeys.Should().NotContain(TraceContextHeaders.TraceParent,
                "ADR-0010 D5 keeps traceparent off MessageContext so no new core key needs a HeaderDisposition here");
            coreKeys.Should().NotContain(TraceContextHeaders.TraceState,
                "ADR-0010 D5 keeps tracestate off MessageContext so no new core key needs a HeaderDisposition here");

            Action initializeMarshaller =
                () => RuntimeHelpers.RunClassConstructor(typeof(RabbitMqHeaderMarshaller).TypeHandle);

            initializeMarshaller.Should().NotThrow(
                "the completeness gate must stay satisfied with the trace-context keys declared outside MessageContext; " +
                "a throw here is the TypeInitializationException ADR-0010 D5 exists to prevent");
        }

        // Non-core treatment is the OBSERVABLE consequence of the placement: a core key carrying a DecodeString
        // disposition would be UTF-8 decoded to a string on the way in, whereas a non-core key is preserved VERBATIM.
        // Getting the SAME byte[] instance back is therefore the proof the marshaller never dispositioned these keys.
        [Fact]
        public void MustTreatTraceContextKeysAsNonCoreAndPreserveThemVerbatim()
        {
            var traceParentBytes = Encoding.UTF8.GetBytes(SampleTraceParent);
            var traceStateBytes = Encoding.UTF8.GetBytes(SampleTraceState);
            var delivered = new Dictionary<string, object>
            {
                [TraceContextHeaders.TraceParent] = traceParentBytes,
                [TraceContextHeaders.TraceState] = traceStateBytes
            };

            var context = RabbitMqHeaderMarshaller.ToContext(delivered);

            context[TraceContextHeaders.TraceParent].Should().BeSameAs(traceParentBytes,
                "a non-core key is preserved verbatim; a decoded string would mean the key had been dispositioned as core");
            context[TraceContextHeaders.TraceState].Should().BeSameAs(traceStateBytes,
                "a non-core key is preserved verbatim; a decoded string would mean the key had been dispositioned as core");
        }

        // --- 2. OUTBOUND: the injected string passes the field-table coercion untouched ---

        [Fact]
        public void MustCoerceOutboundTraceContextToTheWireAsStrings()
        {
            var context = new Dictionary<string, object>
            {
                [TraceContextHeaders.TraceParent] = SampleTraceParent,
                [TraceContextHeaders.TraceState] = SampleTraceState
            };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table[TraceContextHeaders.TraceParent].Should().BeOfType<string>(
                "string is already an AMQP field-table-legal type, so the outbound coercion must pass it through");
            table[TraceContextHeaders.TraceParent].Should().Be(SampleTraceParent);
            table[TraceContextHeaders.TraceState].Should().Be(SampleTraceState);
        }

        // --- 3. INBOUND: the byte[] a real broker delivers is still extractable ---

        // ADR-0010 D5's recorded consequence. Because these keys are non-core they are NOT decoded on the way in, and
        // RabbitMQ surfaces an AMQP longstr in .NET as byte[]. The extractor's byte[] arm is therefore the NORMAL
        // case on this adapter, not defensive coding — this pins it.
        [Fact]
        public void MustExtractTraceContextDeliveredAsAnAmqpLongstrByteArray()
        {
            var delivered = new Dictionary<string, object>
            {
                [TraceContextHeaders.TraceParent] = Encoding.UTF8.GetBytes(SampleTraceParent),
                [TraceContextHeaders.TraceState] = Encoding.UTF8.GetBytes(SampleTraceState)
            };

            var context = new Dictionary<string, object>(RabbitMqHeaderMarshaller.ToContext(delivered));

            context[TraceContextHeaders.TraceParent].Should().BeOfType<byte[]>(
                "the AMQP longstr a real broker delivers surfaces in .NET as byte[] and is preserved verbatim");

            TraceContextPropagator.TryExtract(context, out var extracted).Should().BeTrue(
                "the extractor must UTF-8 decode the byte[] longstr; failing here silently severs the distributed trace");

            extracted.TraceId.ToHexString().Should().Be(SampleTraceId);
            extracted.TraceState.Should().Be(SampleTraceState);
            extracted.IsRemote.Should().BeTrue("the trace context came off the wire from an upstream producer");
        }

        // --- 4. ROUND TRIP: inject -> marshal out -> marshal back -> extract ---

        [Fact]
        public void MustRoundTripAnInjectedTraceContextBackToTheSameTraceId()
        {
            using var producer = new SampledProducerSource();
            using var sendSpan = producer.StartSpan();

            sendSpan.Should().NotBeNull("the test's own sampler must produce a real span to inject from");
            sendSpan.TraceStateString = SampleTraceState;

            var outboundContext = new Dictionary<string, object>();
            TraceContextPropagator.Inject(sendSpan, outboundContext);

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(outboundContext, NewProperties());
            var delivered = EncodeTableAsLongstrDelivery(table);
            var inboundContext = new Dictionary<string, object>(RabbitMqHeaderMarshaller.ToContext(delivered));

            TraceContextPropagator.TryExtract(inboundContext, out var extracted).Should().BeTrue(
                "the trace context must survive the full outbound coercion and inbound preservation round trip");

            extracted.TraceId.ToHexString().Should().Be(sendSpan.TraceId.ToHexString(),
                "trace-id continuity across the hop is what keeps the distributed trace whole");
            extracted.SpanId.ToHexString().Should().Be(sendSpan.SpanId.ToHexString(),
                "the receiving hop must parent to the producing span, not to some other span in the trace");
            extracted.TraceState.Should().Be(SampleTraceState);
        }

        // Re-encodes every string value in an outbound header table as the UTF-8 byte[] a real RabbitMQ broker
        // delivers an AMQP longstr as, so the inbound arm is exercised against the wire shape rather than the
        // in-process string shape.
        private static Dictionary<string, object> EncodeTableAsLongstrDelivery(IDictionary<string, object> table)
        {
            var delivered = new Dictionary<string, object>();

            foreach (var entry in table)
            {
                delivered[entry.Key] = entry.Value is string text ? Encoding.UTF8.GetBytes(text) : entry.Value;
            }

            return delivered;
        }

        // A test-local ActivitySource paired with a .NET BCL System.Diagnostics.ActivityListener (the BCL
        // subscription type — never a RabbitMq Receiver) that samples everything, so the round-trip arm has a real
        // recorded span to inject from without switching Chatter's own diagnostics on.
        private sealed class SampledProducerSource : IDisposable
        {
            private const string SourceName = "Chatter.MessageBrokers.RabbitMQ.Tests.TraceContext";

            private readonly ActivitySource _source;
            private readonly ActivityListener _bclActivityListener;

            public SampledProducerSource()
            {
                _source = new ActivitySource(SourceName);
                _bclActivityListener = new ActivityListener
                {
                    ShouldListenTo = candidate => candidate.Name == SourceName,
                    Sample = SampleAllData
                };

                ActivitySource.AddActivityListener(_bclActivityListener);
            }

            public Activity StartSpan() => _source.StartActivity("send trace-context-round-trip", ActivityKind.Producer);

            public void Dispose()
            {
                _bclActivityListener.Dispose();
                _source.Dispose();
            }

            private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options)
                => ActivitySamplingResult.AllDataAndRecorded;
        }
    }
}
