using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Slips;
using FluentAssertions;
using Moq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingWireFormatParity
{
    // ====================================================================================
    // FROZEN NEWTONSOFT ORACLE — wire-format parity contract.
    //
    // The serialized wire form of brokered-message bodies, routing slips, and the outbox
    // MessageContext dictionary is a CROSS-VERSION COMPATIBILITY CONTRACT: EF outbox rows
    // persist these strings and rolling deploys mean a newer node must deserialize bytes a
    // older node wrote (and vice versa). The Phase-2 port from Newtonsoft.Json to
    // System.Text.Json therefore MUST be byte-identical on output and must still deserialize
    // historical Newtonsoft bytes.
    //
    // This file PINS the exact Newtonsoft output (harvested via probe-and-pin against the real
    // production seams, NOT hand-typed) as golden literal constants. The assertions below run
    // GREEN under the CURRENT Newtonsoft reference. After the STJ port (STEP-PARITY-VERIFY),
    // STJ configured with JavaScriptEncoder.UnsafeRelaxedJsonEncoding MUST reproduce these exact
    // same literals, and STJ deserialization of the golden Newtonsoft-written bytes MUST succeed.
    //
    // HARVESTED ESCAPING TRUTH (the reason UnsafeRelaxedJsonEncoding is the required STJ target):
    //   * Newtonsoft default does NOT HTML-escape: '+', '<', '>', '&', '\'', '/' are emitted
    //     LITERALLY (STJ DEFAULT would escape these to +, <, >, &, ' —
    //     a wire-format BREAK; UnsafeRelaxedJsonEncoding restores the literal form).
    //   * '"' and '\\' use the standard JSON two-char escapes (\" and \\) under both serializers.
    //   * Non-ASCII 'é', 'ü' and the astral/surrogate-pair emoji '😀' (U+1F600) are emitted as
    //     LITERAL UTF-8, NOT \uXXXX-escaped, by Newtonsoft default. STJ default would \uXXXX-escape
    //     all non-ASCII; UnsafeRelaxedJsonEncoding emits them literally to match.
    //
    // OUT OF CONTRACT: raw control characters are intentionally NOT included as parity cases.
    // When the STJ port intentionally changes the wire form, these literals must be updated.
    // ====================================================================================
    public class WhenSerializingRiskyCharacters : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();

        // Risky-character corpus. Covers the chars where STJ-default diverges from Newtonsoft:
        // '+' '<' '>' '&' '\'' '/' '"' '\\' plus non-ASCII 'é' 'ü' and astral emoji '😀' (U+1F600).
        private const string RiskyValue = "+<>&'/\"\\éü\U0001F600";
        // A routing destination path carrying a '/' (path separator) and non-ASCII chars.
        private const string RiskyPath = "queue/path-é-\U0001F600";

        // A fixed slip id so the golden slip wire form is deterministic.
        private static readonly Guid FixedSlipId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        // ---- GOLDEN LITERALS (frozen Newtonsoft oracle; probe-harvested, NOT hand-typed) ----

        // SEAM 1: JsonBodyConverter.Stringify(object) — a POCO whose property VALUE is the full
        // risky corpus. Note the literal '+<>&\'/' and literal UTF-8 'éü😀'; only '"' and '\\' escape.
        private const string GoldenBodyJson =
            "{\"Name\":\"+<>&'/\\\"\\\\éü\U0001F600\"}";

        // SEAM 2: InboundBrokeredMessageExtensions.WithRoutingSlip — the slip string written under
        // MessageContext.RoutingSlip. RiskyPath rides in the first step's DestinationPath. Pins the
        // full property ordering Newtonsoft emits: Id, Route, Attachments, Visited. This same literal
        // is ALSO the deserialize-side golden: a Newtonsoft-written RoutingSlip (Id + 2-step Route with
        // a risky DestinationPath) that STEP-PARITY-VERIFY feeds back through STJ deserialization.
        private const string GoldenSlipJson =
            "{\"Id\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"Route\":[{\"DestinationPath\":\"queue/path-é-\U0001F600\"},{\"DestinationPath\":\"second-step\"}]," +
            "\"Attachments\":{},\"Visited\":[]}";

        // SEAM 3: the outbox MessageContext dictionary serialized via JsonConvert.SerializeObject
        // (InMemoryBrokeredMessageOutbox L49). Header KEYS are fixed Chatter.* constants (no risky
        // chars — keys are not a risky surface); the risky corpus rides in the CorrelationId VALUE.
        // This same literal is ALSO the deserialize-side golden outbox MessageContext dict for
        // STEP-PARITY-VERIFY.
        private const string GoldenOutboxContextJson =
            "{\"Chatter.ContentType\":\"application/json\"," +
            "\"Chatter.CorrelationId\":\"+<>&'/\\\"\\\\éü\U0001F600\"}";

        public WhenSerializingRiskyCharacters()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private class BodyPoco
        {
            public string Name { get; set; }
        }

        [Fact]
        public void MustEmitGoldenNewtonsoftBodyJsonForRiskyPropertyValue()
        {
            var json = new JsonBodyConverter().Stringify(new BodyPoco { Name = RiskyValue });
            json.Should().Be(GoldenBodyJson);
        }

        // STEP-000B FEASIBILITY GATE (Option A byte-for-byte parity):
        // Proves the STJ port — JsonBodyConverter.Stringify now routes through ChatterJson.Options
        // (ChatterJsonEncoder) — emits the astral emoji 😀 (U+1F600) as LITERAL UTF-8, NOT as a
        // 😀 surrogate escape. UnsafeRelaxedJsonEscaping alone surrogate-escapes astral
        // scalars; ChatterJsonEncoder forcing all non-ASCII literal is what makes Option A achievable.
        // Asserted BYTE-FOR-BYTE on the UTF-8 encoding of the output against the frozen golden literal.
        // If STJ's writer hard-escaped surrogate pairs regardless of the encoder, this would go RED.
        [Fact]
        public void MustEmitAstralScalarAsLiteralUtf8ByteForByteThroughStjPort()
        {
            var json = new JsonBodyConverter().Stringify(new BodyPoco { Name = RiskyValue });

            var actualBytes = System.Text.Encoding.UTF8.GetBytes(json);
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(GoldenBodyJson);

            actualBytes.Should().Equal(expectedBytes);

            // Pin the literal 4-byte UTF-8 encoding of 😀 (U+1F600 -> F0 9F 98 80) is present and that
            // the surrogate-escape form "😀" is absent.
            json.Should().Contain("\U0001F600");
            json.Should().NotContain("\\u");
        }

        [Fact]
        public void MustEmitGoldenNewtonsoftSlipJsonForRiskyDestinationPath()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(FixedSlipId)
                .WithRoute(RiskyPath)
                .WithRoute("second-step")
                .Build();

            // SERIALIZE seam: WithRoutingSlip writes the serialized slip string into the
            // message-context dictionary under MessageContext.RoutingSlip.
            var inbound = new InboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", _bodyConverter.Object);
            inbound.WithRoutingSlip(slip);

            var serializedSlip = (string)inbound.MessageContextImpl[MessageContext.RoutingSlip];
            serializedSlip.Should().Be(GoldenSlipJson);
        }

        [Fact]
        public void MustEmitGoldenNewtonsoftOutboxContextJsonForRiskyHeaderValue()
        {
            // Mirrors InMemoryBrokeredMessageOutbox: JsonConvert.SerializeObject over the
            // MessageContext dictionary. Keys are the fixed Chatter.* header constants.
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.ContentType] = "application/json",
                [MessageContext.CorrelationId] = RiskyValue
            };

            JsonConvert.SerializeObject(messageContext).Should().Be(GoldenOutboxContextJson);
        }

        // ---- Deserialize-side sanity gates (Newtonsoft today; STJ asserts come in STEP-PARITY-VERIFY) ----

        [Fact]
        public void MustDeserializeGoldenNewtonsoftSlipBytesThroughProductionSeam()
        {
            // Feed the golden Newtonsoft-written slip bytes back through the real deserialize seam
            // (TryGetRoutingSlip). STEP-PARITY-VERIFY repeats this assertion under STJ to prove STJ
            // can read historical Newtonsoft bytes.
            var deserializeContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = GoldenSlipJson
            };
            var sut = new MessageBrokerContext("message-id", new byte[] { 1 }, deserializeContext, "receiver-path", default, _bodyConverter.Object);

            var found = sut.TryGetRoutingSlip(out var slip);

            found.Should().BeTrue();
            slip.Id.Should().Be(FixedSlipId);
            slip.Route.Should().HaveCount(2);
            slip.Route[0].DestinationPath.Should().Be(RiskyPath);
            slip.Route[1].DestinationPath.Should().Be("second-step");
        }

        [Fact]
        public void MustDeserializeGoldenNewtonsoftOutboxContextBytes()
        {
            // Feed the golden Newtonsoft-written outbox MessageContext bytes back through Newtonsoft
            // today; STEP-PARITY-VERIFY repeats under STJ. Confirms the risky CorrelationId value
            // survives a full round-trip intact.
            var roundTripped = JsonConvert.DeserializeObject<Dictionary<string, object>>(GoldenOutboxContextJson);

            roundTripped.Should().ContainKey(MessageContext.CorrelationId)
                .WhoseValue.Should().Be(RiskyValue);
            roundTripped.Should().ContainKey(MessageContext.ContentType)
                .WhoseValue.Should().Be("application/json");
        }
    }
}
