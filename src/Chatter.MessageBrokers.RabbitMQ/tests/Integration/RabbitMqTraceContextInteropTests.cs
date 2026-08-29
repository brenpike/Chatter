using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using RabbitMQ.Client;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // W3C trace-context interoperability between Chatter's own broker instrumentation and RabbitMQ.Client 7.2.1's,
    // proven against a REAL broker. Two matrix cells:
    //
    //   (a) Chatter instrumented, the SDK NOT — the SDK's publish-side instrumentation is gated on
    //       RabbitMQActivitySource.PublisherHasListeners, which stays false while nothing subscribes to its own
    //       ActivitySource, so the traceparent that reaches the handler is byte-for-byte the one Chatter wrote.
    //
    //   (b) BOTH instrumented — the SDK starts its own publish span and re-injects its own traceparent over
    //       Chatter's (last writer wins). What matters is not who wrote the header last but that the SDK's span is a
    //       DESCENDANT of the Chatter send span and the delivered traceparent still carries the SAME trace id, so the
    //       distributed trace survives the hop either way.
    //
    // VACUITY GUARD: cell (b) asserts the SDK actually emitted a span before it asserts anything about those spans.
    // A matrix that silently proves nothing because the SDK was never active is worse than no matrix, and that exact
    // risk is why this test exists.
    //
    // "ActivityListener" below always means the .NET BCL System.Diagnostics.ActivityListener subscription type; it is
    // never a RabbitMq Receiver.
    //
    // The facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the nightly RabbitMQ CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqTraceContextInteropTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        // The SDK's publish-side gate. It is internal on RabbitMQ.Client 7.2.1, so it is read reflectively rather
        // than assumed from documentation; the lookup asserts it still exists, so a package upgrade that renames or
        // removes the gate fails this test loudly instead of quietly voiding the premise.
        private const string PublisherGatePropertyName = "PublisherHasListeners";

        private readonly RabbitMqFixture _fixture;

        public RabbitMqTraceContextInteropTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // A command whose only job is to carry a trace context across a real broker hop.
        public sealed class TraceContextCommand : ICommand
        {
            public string Name { get; set; }
        }

        // MATRIX CELL (a): Chatter instrumented, RabbitMQ.Client NOT. The SDK's publish path must stay dark and
        // Chatter's traceparent must reach the handler unmodified.
        [RequiresDockerFact]
        public async Task ChatterOwnsTheTraceParentWhileTheSdkPublisherInstrumentationStaysOff()
        {
            var publisherGate = ResolvePublisherGate();

            // Package contract, read off the RESOLVED RabbitMQ.Client assembly rather than trusted: the source the
            // gate guards is the one named by the SDK's own public constant.
            RabbitMQActivitySource.PublisherSourceName.Should().Be("RabbitMQ.Client.Publisher",
                "the SDK's publish-side ActivitySource name is the subscription point cell (b) attaches to");
            ReadPublisherGate(publisherGate).Should().BeFalse(
                "RabbitMQ.Client's publish-side instrumentation is off until something subscribes to its own ActivitySource");

            var set = RabbitMqTopology.CreateSet("traceparent", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            using var spanRecorder = new SpanRecorder(BrokerDiagnostics.ActivitySourceName);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<TraceContextCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(TraceContextCommand));
            try
            {
                await harness.StartAsync();

                await harness.SendToQueueAsync(new TraceContextCommand { Name = "chatter-only" }, set.WorkQueueName);
                var handled = await harness.WaitForHandledAsync<TraceContextCommand>(HandlerWait);

                ReadPublisherGate(publisherGate).Should().BeFalse(
                    "subscribing to Chatter's ActivitySource must not switch RabbitMQ.Client's own instrumentation on");

                var sendSpan = ResolveSendSpan(spanRecorder, set.WorkQueueName);
                var deliveredTraceParent = ReadDeliveredTraceParent(handled.Context.BrokeredMessage.MessageContext);

                // Activity.Id in the W3C id format IS the traceparent, so an exact match proves nothing rewrote the
                // header between Chatter's injection and the delivery.
                deliveredTraceParent.Should().Be(sendSpan.Id,
                    "with the SDK's publish instrumentation off, the delivered traceparent must be exactly the one Chatter wrote");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // MATRIX CELL (b): BOTH instrumented. The SDK re-injects its own traceparent over Chatter's; trace-id
        // continuity must survive that overwrite and the SDK's publish span must sit inside Chatter's trace.
        [RequiresDockerFact]
        public async Task SdkPublisherSpanStaysInTheChatterTraceWhenBothInstrumentationsAreActive()
        {
            var publisherGate = ResolvePublisherGate();

            var set = RabbitMqTopology.CreateSet("sdktrace", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            using var spanRecorder = new SpanRecorder(
                BrokerDiagnostics.ActivitySourceName,
                RabbitMQActivitySource.PublisherSourceName);

            ReadPublisherGate(publisherGate).Should().BeTrue(
                "attaching a .NET ActivityListener to the SDK's publisher source must switch its publish instrumentation on");

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<TraceContextCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(TraceContextCommand));
            try
            {
                await harness.StartAsync();

                await harness.SendToQueueAsync(new TraceContextCommand { Name = "chatter-and-sdk" }, set.WorkQueueName);
                var handled = await harness.WaitForHandledAsync<TraceContextCommand>(HandlerWait);

                var sendSpan = ResolveSendSpan(spanRecorder, set.WorkQueueName);
                var sdkSpans = spanRecorder.SpansFrom(RabbitMQActivitySource.PublisherSourceName);

                // VACUITY GUARD — everything below is meaningless unless the SDK really instrumented this publish.
                sdkSpans.Should().NotBeEmpty(
                    "the both-instrumented matrix cell proves nothing unless RabbitMQ.Client's own publish-side " +
                    "instrumentation actually emitted a span; an empty set means the SDK was never active");
                sdkSpans.Should().ContainSingle(
                    "one dispatch call publishes one message, so the SDK must emit exactly one publish span for it");

                var sdkPublishSpan = sdkSpans[0];

                sdkPublishSpan.TraceId.ToHexString().Should().Be(sendSpan.TraceId.ToHexString(),
                    "the SDK's publish span must be a descendant of the Chatter send span, so one trace covers the hop");
                sdkPublishSpan.ParentSpanId.ToHexString().Should().Be(sendSpan.SpanId.ToHexString(),
                    "the Chatter send span is ambient while the SDK publishes, so the SDK span parents directly to it");

                var deliveredTraceParent = ReadDeliveredTraceParent(handled.Context.BrokeredMessage.MessageContext);

                deliveredTraceParent.Should().NotBe(sendSpan.Id,
                    "the SDK re-injects its own traceparent over Chatter's — this pins that last-writer-wins overwrite " +
                    "as observed behaviour rather than assuming it away");

                ActivityContext.TryParse(deliveredTraceParent, null, isRemote: true, out var deliveredContext)
                    .Should().BeTrue("the delivered traceparent must remain a well-formed W3C trace context");

                // The overwrite is harmless precisely because the span it names is inside Chatter's trace: the trace
                // id is unchanged and the span id is the SDK's own descendant span.
                deliveredContext.TraceId.ToHexString().Should().Be(sendSpan.TraceId.ToHexString(),
                    "the SDK's last-writer-wins overwrite must still carry the Chatter send span's trace id");
                deliveredContext.SpanId.ToHexString().Should().Be(sdkPublishSpan.SpanId.ToHexString(),
                    "the delivered traceparent names the SDK's publish span, which is itself a descendant of the Chatter send span");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // The SDK's publish-side gate, looked up on the resolved package. Fails loudly when the member is gone so an
        // upgrade cannot silently void this test's premise.
        private static PropertyInfo ResolvePublisherGate()
        {
            var publisherGate = typeof(RabbitMQActivitySource)
                .GetProperty(PublisherGatePropertyName, BindingFlags.NonPublic | BindingFlags.Static);

            publisherGate.Should().NotBeNull(
                $"RabbitMQ.Client's publish-side instrumentation gates on RabbitMQActivitySource.{PublisherGatePropertyName}; " +
                "its absence means the resolved package no longer works the way this test asserts");

            return publisherGate;
        }

        private static bool ReadPublisherGate(PropertyInfo publisherGate) => (bool)publisherGate.GetValue(null);

        // The single Chatter send span for this scenario's destination. Filtering on the destination tag keeps the
        // lookup exact even though the same ActivitySource also emits the receive span for this delivery.
        private static Activity ResolveSendSpan(SpanRecorder spanRecorder, string destinationName)
        {
            var sendSpans = spanRecorder.SpansFrom(BrokerDiagnostics.ActivitySourceName)
                .Where(span => span.Kind == ActivityKind.Producer)
                .Where(span => (string)span.GetTagItem(BrokerDiagnostics.DestinationName) == destinationName)
                .ToList();

            sendSpans.Should().ContainSingle(
                "the scenario dispatches exactly one message to this destination, so Chatter must emit exactly one send span");

            return sendSpans[0];
        }

        // The traceparent as it arrived on the inbound context. It is a NON-core key, so the marshaller preserves it
        // verbatim and a real broker delivers the AMQP longstr as byte[] (ADR-0010 D5).
        private static string ReadDeliveredTraceParent(IReadOnlyDictionary<string, object> messageContext)
        {
            messageContext.Should().ContainKey(TraceContextHeaders.TraceParent,
                "Chatter must write the W3C trace context onto the outbound message when its diagnostics are on");

            var deliveredValue = messageContext[TraceContextHeaders.TraceParent];

            return deliveredValue is byte[] longstr ? Encoding.UTF8.GetString(longstr) : deliveredValue as string;
        }

        // Records every span the named ActivitySources start, through a .NET BCL System.Diagnostics.ActivityListener
        // (the BCL subscription type — never a RabbitMq Receiver). Attaching it is also what switches each source's
        // HasListeners guard on, which is the matrix dimension these tests vary.
        private sealed class SpanRecorder : IDisposable
        {
            private readonly ConcurrentBag<Activity> _startedSpans = new ConcurrentBag<Activity>();
            private readonly HashSet<string> _sourceNames;
            private readonly ActivityListener _bclActivityListener;

            public SpanRecorder(params string[] sourceNames)
            {
                _sourceNames = new HashSet<string>(sourceNames, StringComparer.Ordinal);
                _bclActivityListener = new ActivityListener
                {
                    ShouldListenTo = candidate => _sourceNames.Contains(candidate.Name),
                    Sample = SampleAllData,
                    ActivityStarted = _startedSpans.Add
                };

                ActivitySource.AddActivityListener(_bclActivityListener);
            }

            public IReadOnlyList<Activity> SpansFrom(string sourceName)
                => _startedSpans.Where(span => span.Source.Name == sourceName).ToList();

            public void Dispose() => _bclActivityListener.Dispose();

            private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options)
                => ActivitySamplingResult.AllDataAndRecorded;
        }
    }
}
