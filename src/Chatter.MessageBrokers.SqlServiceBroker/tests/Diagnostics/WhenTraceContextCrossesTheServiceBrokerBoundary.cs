using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Diagnostics
{
    /// <summary>
    /// Pins ADR-0010's "Propagation scope" section for SQL Service Broker: which receive paths carry W3C Trace
    /// Context across the boundary and which drop it. The two dropping paths are PRE-EXISTING limitations that
    /// predate telemetry and affect EVERY header, not just trace context; they are pinned here so a future change
    /// that either fixes or worsens them shows up as a failing test rather than as silent behaviour drift.
    /// </summary>
    /// <remarks>
    /// -----------------------------------------------------------------------------------------------
    /// REACHABLE-vs-DEFERRED LEDGER (mirrors WhenReceivingMessageWithTypedHeaders' ledger style)
    /// -----------------------------------------------------------------------------------------------
    /// DEFERRED (live-broker-only): <c>SqlServiceBrokerReceiver.ReceiveMessageAsync</c> opens a real
    ///   <c>SqlConnection</c> and issues a RECEIVE against a Service Broker queue before any header dictionary is
    ///   built, and <c>SqlServiceBrokerReceiver.DeadletterMessageAsync</c> runs an END CONVERSATION plus a nested
    ///   dispatch through a DI scope. Neither can be driven without a live broker, so the end-to-end walk is
    ///   deferred to the Docker-gated integration suite.
    /// REACHABLE (pinned here): the unit-reachable boundary is the exact per-path HEADER TRANSFORMATION the
    ///   receiver applies once a message has been classified. Each helper below reproduces one production branch
    ///   verbatim — the envelope branch (SqlServiceBrokerReceiver.cs:178-195 plus the shared stamping block at
    ///   217-224), the DefaultType branch (the fresh dictionary at :172 plus that same stamping block), and the
    ///   deadletter dictionary literal (:347-355) — and asserts what trace context survives it. Classification
    ///   itself is driven through the PRODUCTION <see cref="ServiceBrokerMessageClassifier"/>, so the claim that
    ///   the lossy branch is the live one for a DEFAULT-typed message is measured rather than assumed.
    /// -----------------------------------------------------------------------------------------------
    /// </remarks>
    public class WhenTraceContextCrossesTheServiceBrokerBoundary : Testing.Core.Context
    {
        private const string ReceiverPath = "ssb-trace-context-receiver";
        private const string SampledTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        private const string TraceStateValue = "congo=t61rcWkgMzE,rojo=00f067aa0ba902b7";
        private const string ApplicationHeaderKey = "x-application-header";
        private const string ApplicationHeaderValue = "application-supplied";

        private static readonly Guid _conversationHandle = Guid.Parse("2f4b3c1e-9a71-4a2e-8b0d-5c6e7f801234");
        private static readonly Guid _conversationGroupHandle = Guid.Parse("6d1a8e57-4c33-4f9b-9a10-7b2c3d4e5f60");

        // The producer-side context a Chatter sender hands to SqlServiceBrokerSender: trace context plus one
        // ordinary application header, so every assertion below can distinguish "trace context was dropped" from
        // "all producer headers were dropped".
        private static Dictionary<string, object> BuildProducerContext(string traceParent = SampledTraceParent)
            => new Dictionary<string, object>
            {
                [TraceContextHeaders.TraceParent] = traceParent,
                [TraceContextHeaders.TraceState] = TraceStateValue,
                [ApplicationHeaderKey] = ApplicationHeaderValue,
                [SSBMessageContext.MessageTypeName] = ServicesMessageTypes.ChatterBrokeredMessageType,
                [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType,
            };

        // The ENVELOPE path, both halves. SqlServiceBrokerSender.SendMessageOnConversation serializes the whole
        // OutboundBrokeredMessage through the SSB-configured body converter when the context supplies
        // MessageTypeName == ChatterBrokeredMessageType (SqlServiceBrokerSender.cs:123-129); the receiver decodes
        // that envelope with the same converter and adopts its MessageContext wholesale
        // (SqlServiceBrokerReceiver.cs:178-195).
        private static IDictionary<string, object> RoundTripThroughEnvelopePath(IDictionary<string, object> producerContext)
        {
            var envelopeConverter = new JsonUnicodeBodyConverter();

            var outbound = new OutboundBrokeredMessage(
                messageId: "envelope-message-id",
                body: new byte[] { 1, 2, 3 },
                messageContext: producerContext,
                destination: ReceiverPath,
                bodyConverter: envelopeConverter);

            byte[] wire = envelopeConverter.Convert(outbound);
            var envelope = envelopeConverter.Convert<OutboundBrokeredMessage>(wire);

            var headers = envelope.MessageContext ?? new Dictionary<string, object>();
            StampServiceBrokerHeaders(headers, ServicesMessageTypes.ChatterBrokeredMessageType);
            return headers;
        }

        // The DEFAULTTYPE path. The receiver initialises `headers` to a FRESH dictionary
        // (SqlServiceBrokerReceiver.cs:172) and only the envelope branch replaces it, so a DEFAULT-typed message
        // reaches the stamping block below with nothing of the producer's context in hand. That this helper takes
        // NO producer context is the point: there is no seam through which one could be supplied.
        private static IDictionary<string, object> BuildDefaultTypePathHeaders()
        {
            IDictionary<string, object> headers = new Dictionary<string, object>();
            StampServiceBrokerHeaders(headers, ServicesMessageTypes.DefaultType);
            return headers;
        }

        // The shared stamping block every non-deadletter receive path runs (SqlServiceBrokerReceiver.cs:217-224).
        private static void StampServiceBrokerHeaders(IDictionary<string, object> headers, string messageTypeName)
        {
            headers[SSBMessageContext.ConversationGroupId] = _conversationGroupHandle;
            headers[SSBMessageContext.ConversationHandle] = _conversationHandle;
            headers[SSBMessageContext.MessageSequenceNumber] = 1L;
            headers[SSBMessageContext.ServiceName] = "trace-context-service";
            headers[SSBMessageContext.ServiceContractName] = ServicesMessageTypes.ChatterServiceContract;
            headers[SSBMessageContext.MessageTypeName] = messageTypeName;
            headers[MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType;
            headers[MessageContext.ReceiveAttempts] = 1;
        }

        // The DEADLETTER path's dictionary literal, reproduced verbatim from
        // SqlServiceBrokerReceiver.DeadletterMessageAsync (SqlServiceBrokerReceiver.cs:347-355).
        private static IDictionary<string, object> BuildDeadletterHeaders()
            => new Dictionary<string, object>
            {
                [SSBMessageContext.ConversationHandle] = _conversationHandle,
                [SSBMessageContext.ServiceName] = "trace-context-service",
                [MessageContext.FailureDescription] = "deadletter-description",
                [MessageContext.FailureDetails] = "deadletter-reason",
                [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType,
                [SSBMessageContext.MessageTypeName] = ServicesMessageTypes.ChatterBrokeredMessageType,
                [SSBMessageContext.ServiceContractName] = ServicesMessageTypes.ChatterServiceContract,
                [MessageContext.ReceiveAttempts] = 1,
            };

        private static ReceivedMessage BuildReceivedMessage(string messageTypeName)
            => new ReceivedMessage(
                convGroupHandle: _conversationGroupHandle,
                convHandle: _conversationHandle,
                messageSeqNo: 1L,
                serviceName: "trace-context-service",
                serviceContractName: ServicesMessageTypes.ChatterServiceContract,
                messageTypeName: messageTypeName,
                body: new byte[] { 1, 2, 3 });

        // -----------------------------------------------------------------------------------------------
        // ENVELOPE PATH — trace context SURVIVES.
        // -----------------------------------------------------------------------------------------------

        [Fact]
        public void MustRoundTripTraceContextThroughTheChatterEnvelope()
        {
            var headers = RoundTripThroughEnvelopePath(BuildProducerContext());

            headers.Should().ContainKey(TraceContextHeaders.TraceParent);
            headers[TraceContextHeaders.TraceParent].Should().Be(SampledTraceParent);
            headers.Should().ContainKey(TraceContextHeaders.TraceState);
            headers[TraceContextHeaders.TraceState].Should().Be(TraceStateValue);
        }

        // INVARIANT: the SSB stamping block runs AFTER the envelope's context is adopted and writes only SSB and
        // core keys, so it can never clobber the producer's trace context or its ordinary application headers.
        [Fact]
        public void MustPreserveApplicationHeadersAlongsideTraceContextThroughTheChatterEnvelope()
        {
            var headers = RoundTripThroughEnvelopePath(BuildProducerContext());

            headers[ApplicationHeaderKey].Should().Be(ApplicationHeaderValue);
            headers[SSBMessageContext.MessageTypeName].Should().Be(ServicesMessageTypes.ChatterBrokeredMessageType);
            headers[MessageContext.ReceiveAttempts].Should().Be(1);
        }

        // The round-tripped context must be usable as a REMOTE PARENT, not merely present as two strings: this is
        // what makes the receive span join the producer's trace rather than start a new one.
        [Fact]
        public void MustExposeTheRoundTrippedEnvelopeContextAsAParsableRemoteParent()
        {
            var headers = RoundTripThroughEnvelopePath(BuildProducerContext());

            TraceContextPropagator.TryExtract((IReadOnlyDictionary<string, object>)headers, out var extracted)
                                  .Should().BeTrue("the envelope path preserves the producer's trace context verbatim");

            extracted.TraceId.ToHexString().Should().Be("0af7651916cd43dd8448eb211c80319c");
            extracted.SpanId.ToHexString().Should().Be("b7ad6b7169203331");
            extracted.TraceFlags.Should().Be(ActivityTraceFlags.Recorded);
            extracted.TraceState.Should().Be(TraceStateValue);
            extracted.IsRemote.Should().BeTrue();
        }

        // -----------------------------------------------------------------------------------------------
        // JSON MATERIALIZATION — a traceparent must NOT be coerced into a DateTime.
        //
        // ADR-0010 reasons that a W3C traceparent survives MessageContext's materialization as a string because
        // it is not ISO-8601-shaped, and records that as REASONED BUT NOT EXECUTED. These tests execute it. The
        // coercion is real for genuinely ISO-8601 strings (MessageContext.MaterializeJsonElement routes
        // JsonValueKind.String through JsonElement.TryGetDateTime to match Newtonsoft's default untyped read), so
        // a mangled traceparent would be a real defect rather than a test to relax.
        // -----------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(SampledTraceParent)]
        [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00")]
        [InlineData("00-12345678901234567890123456789012-1234567890123456-01")]
        public void MustNotCoerceTraceParentToDateTimeOnMaterialization(string traceParent)
        {
            var persisted = ParsePersistedContext(traceParent);

            var materialized = MessageContext.MaterializePersistedContext(persisted);

            materialized[TraceContextHeaders.TraceParent].Should().BeOfType<string>(
                "a W3C traceparent is not ISO-8601-shaped, so JsonElement.TryGetDateTime must decline it and the raw string must survive");
            materialized[TraceContextHeaders.TraceParent].Should().Be(traceParent);
            materialized[TraceContextHeaders.TraceState].Should().BeOfType<string>().And.Be(TraceStateValue);
        }

        [Fact]
        public void MustKeepTraceContextAsStringsThroughTheEnvelopeRoundTrip()
        {
            var headers = RoundTripThroughEnvelopePath(BuildProducerContext());

            headers[TraceContextHeaders.TraceParent].Should().BeOfType<string>(
                "the envelope is decoded through ChatterJson.Options, whose MaterializingObjectConverter shares MessageContext's materialization recipe");
            headers[TraceContextHeaders.TraceState].Should().BeOfType<string>();
        }

        // The control for the two tests above: the SAME materialization recipe DOES coerce a genuinely
        // ISO-8601-shaped string, so their pass is a property of the traceparent's shape rather than of a
        // materializer that never coerces anything.
        [Fact]
        public void MustStillCoerceAnIso8601HeaderSoTheTraceParentResultIsMeaningful()
        {
            var persisted = ParsePersistedContext(SampledTraceParent, "2026-08-29T14:24:28Z");

            var materialized = MessageContext.MaterializePersistedContext(persisted);

            materialized["scheduled-at"].Should().BeOfType<DateTime>(
                "an ISO-8601 string IS coerced, which is why the traceparent assertions are worth executing");
        }

        // Reproduces the persisted shape MessageContext.MaterializePersistedContext is fed: object-typed values
        // that arrived as raw System.Text.Json JsonElements.
        private static IDictionary<string, object> ParsePersistedContext(string traceParent, string scheduledAt = null)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [TraceContextHeaders.TraceParent] = traceParent,
                [TraceContextHeaders.TraceState] = TraceStateValue,
                ["scheduled-at"] = scheduledAt ?? "not-a-date",
            });

            using var document = JsonDocument.Parse(json);

            var persisted = new Dictionary<string, object>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                persisted[property.Name] = property.Value.Clone();
            }

            return persisted;
        }

        // -----------------------------------------------------------------------------------------------
        // DEFAULTTYPE PATH — trace context is LOST.
        //
        // PRE-EXISTING and NOT introduced by telemetry: the receiver builds a fresh header dictionary for every
        // delivery and only the Chatter envelope branch replaces it, so a DEFAULT-typed message loses EVERY
        // producer-supplied header. Trace context is simply one more casualty of that, which is why the
        // application-header assertion sits beside the trace-context one below.
        // -----------------------------------------------------------------------------------------------

        [Fact]
        public void MustClassifyADefaultTypedMessageOntoTheContextDroppingBranch()
        {
            var outcome = new ServiceBrokerMessageClassifier().Classify(BuildReceivedMessage(ServicesMessageTypes.DefaultType));

            outcome.Should().Be(ClassificationOutcome.DispatchDefault,
                "a DEFAULT-typed delivery dispatches the raw body without unwrapping an envelope, which is the branch that keeps the fresh header dictionary");
        }

        [Fact]
        public void MustDropTraceContextOnTheDefaultTypePath()
        {
            var headers = BuildDefaultTypePathHeaders();

            headers.Should().NotContainKey(TraceContextHeaders.TraceParent);
            headers.Should().NotContainKey(TraceContextHeaders.TraceState);
        }

        [Fact]
        public void MustDropOrdinaryApplicationHeadersOnTheDefaultTypePathToo()
        {
            var headers = BuildDefaultTypePathHeaders();

            headers.Should().NotContainKey(ApplicationHeaderKey,
                "the DefaultType path drops ALL producer context, so this limitation is not specific to trace context");

            headers.Keys.Should().BeEquivalentTo(new[]
            {
                SSBMessageContext.ConversationGroupId,
                SSBMessageContext.ConversationHandle,
                SSBMessageContext.MessageSequenceNumber,
                SSBMessageContext.ServiceName,
                SSBMessageContext.ServiceContractName,
                SSBMessageContext.MessageTypeName,
                MessageContext.InfrastructureType,
                MessageContext.ReceiveAttempts,
            }, "the receiver stamps exactly these keys and nothing the producer supplied reaches them");
        }

        [Fact]
        public void MustExtractNoRemoteParentFromADefaultTypeDelivery()
        {
            var headers = BuildDefaultTypePathHeaders();

            TraceContextPropagator.TryExtract((IReadOnlyDictionary<string, object>)headers, out var extracted)
                                  .Should().BeFalse("no traceparent reaches a DEFAULT-typed delivery, so its receive span is a fresh root");

            extracted.Should().Be(default(ActivityContext));
        }

        // -----------------------------------------------------------------------------------------------
        // DEADLETTER PATH — trace context is LOST.
        //
        // PRE-EXISTING and NOT introduced by telemetry, and lossy in the same all-headers way: the deadletter
        // dispatch builds its outbound context from a fresh dictionary literal rather than from the failed
        // delivery's inbound context, so a deadlettered message cannot be correlated to the trace that produced it.
        // -----------------------------------------------------------------------------------------------

        [Fact]
        public void MustCarryNoTraceContextOnTheDeadletterPath()
        {
            var headers = BuildDeadletterHeaders();

            headers.Should().NotContainKey(TraceContextHeaders.TraceParent);
            headers.Should().NotContainKey(TraceContextHeaders.TraceState);
            headers.Should().NotContainKey(ApplicationHeaderKey);
        }

        [Fact]
        public void MustExtractNoRemoteParentFromTheDeadletteredMessage()
        {
            var headers = BuildDeadletterHeaders();

            TraceContextPropagator.TryExtract((IReadOnlyDictionary<string, object>)headers, out _)
                                  .Should().BeFalse("the deadletter dispatch starts from a fresh dictionary literal, so nothing upstream survives it");
        }
    }
}
