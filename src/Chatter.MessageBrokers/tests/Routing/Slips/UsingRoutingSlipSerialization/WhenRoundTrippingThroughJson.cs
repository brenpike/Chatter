using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Slips;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingRoutingSlipSerialization
{
    // CHARACTERIZATION TEST: pins the CURRENT Newtonsoft.Json round-trip behavior of RoutingSlip
    // through the real production seams (InboundBrokeredMessage.WithRoutingSlip serialize +
    // MessageBrokerContext.TryGetRoutingSlip deserialize). It exists so the planned
    // Newtonsoft -> System.Text.Json port can be verified to preserve observable round-trip behavior.
    //
    // INVARIANT: assertions pin round-trip FIDELITY (Id, Route count, ordered DestinationPath),
    // NOT the exact JSON wire bytes. The wire format is a serializer-internal detail that the STJ
    // port is permitted to change, so asserting exact bytes here would falsely fail the port.
    public class WhenRoundTrippingThroughJson : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();

        public WhenRoundTrippingThroughJson()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private MessageBrokerContext CreateContext(IDictionary<string, object> messageContext)
            => new MessageBrokerContext("message-id", new byte[] { 1 }, messageContext, "receiver-path", CancellationToken.None, _bodyConverter.Object);

        [Fact]
        public void MustRoundTripRoutingSlipThroughProductionSeams()
        {
            var id = Guid.NewGuid();

            // Use a NON-EMPTY Route so that RoutingStep's private parameterless [JsonConstructor]
            // is the load-bearing deserialize mechanism on the way back. The Phase-2 STJ port MUST
            // preserve binding to this non-public constructor or this round-trip breaks.
            var slip = RoutingSlipBuilder.NewRoutingSlip(id)
                .WithRoute("a")
                .WithRoute("b")
                .Build();

            // SERIALIZE seam: the dictionary instance handed to the context is the same instance the
            // InboundBrokeredMessage wraps, so WithRoutingSlip writes the serialized slip string into it
            // under MessageContext.RoutingSlip.
            var serializeContext = new Dictionary<string, object>();
            var serializeSut = CreateContext(serializeContext);
            serializeSut.BrokeredMessage.WithRoutingSlip(slip);

            serializeContext.Should().ContainKey(MessageContext.RoutingSlip);
            var serializedSlip = serializeContext[MessageContext.RoutingSlip];

            // DESERIALIZE seam: seed the serialized slip string into a fresh context's message-context
            // dictionary, then deserialize through TryGetRoutingSlip.
            var deserializeContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = serializedSlip
            };
            var deserializeSut = CreateContext(deserializeContext);

            var found = deserializeSut.TryGetRoutingSlip(out var roundTrippedSlip);

            found.Should().BeTrue();
            roundTrippedSlip.Id.Should().Be(id);
            roundTrippedSlip.Route.Should().HaveCount(2);
            roundTrippedSlip.Route[0].DestinationPath.Should().Be("a");
            roundTrippedSlip.Route[1].DestinationPath.Should().Be("b");
        }

        // VISITED + ATTACHMENTS FIDELITY GATE (the MED finding): the prior round-trip pinned only an empty
        // Visited / empty Attachments, so a regression that dropped a populated Visited list (or Attachments
        // entry) would not be caught. This advances the slip with RouteToNextStep() so _visited is non-empty
        // BEFORE serialize, seeds a non-empty Attachments entry, then round-trips through the real production
        // seams (WithRoutingSlip serialize -> TryGetRoutingSlip deserialize) and asserts both survive. Visited
        // binds back through RoutingSlip.Visited's [JsonInclude] private setter, which the STJ port must preserve.
        [Fact]
        public void MustRoundTripNonEmptyVisitedAndAttachmentsThroughProductionSeams()
        {
            var id = Guid.NewGuid();

            var slip = RoutingSlipBuilder.NewRoutingSlip(id)
                .WithRoute("first")
                .WithRoute("second")
                .WithRoute("third")
                .Build();

            // Advance the slip so the first two steps move into Visited (in order) and only "third" remains in
            // Route. This makes _visited non-empty so the serialize/deserialize of Visited is load-bearing.
            slip.RouteToNextStep().Should().Be("first");
            slip.RouteToNextStep().Should().Be("second");
            slip.Visited.Should().HaveCount(2);

            // Seed non-empty Attachments entries carrying primitives (internal setter is visible to this
            // test assembly). After round-trip these must materialize back to their CLR types, not JsonElement.
            slip.Attachments["attachment-key"] = "attachment-value";
            slip.Attachments["attachment-number"] = 42;

            // SERIALIZE seam.
            var serializeContext = new Dictionary<string, object>();
            var serializeSut = CreateContext(serializeContext);
            serializeSut.BrokeredMessage.WithRoutingSlip(slip);

            serializeContext.Should().ContainKey(MessageContext.RoutingSlip);
            var serializedSlip = serializeContext[MessageContext.RoutingSlip];

            // DESERIALIZE seam.
            var deserializeContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = serializedSlip
            };
            var deserializeSut = CreateContext(deserializeContext);

            var found = deserializeSut.TryGetRoutingSlip(out var roundTrippedSlip);

            found.Should().BeTrue();
            roundTrippedSlip.Id.Should().Be(id);

            // Route retains the single un-visited step.
            roundTrippedSlip.Route.Should().HaveCount(1);
            roundTrippedSlip.Route[0].DestinationPath.Should().Be("third");

            // Visited survives with the correct count and ORDERED DestinationPaths.
            roundTrippedSlip.Visited.Should().HaveCount(2);
            roundTrippedSlip.Visited[0].DestinationPath.Should().Be("first");
            roundTrippedSlip.Visited[1].DestinationPath.Should().Be("second");

            // Attachments survives AND its primitive values are materialized back to the CLR types
            // Newtonsoft's untyped read produced (NOT raw JsonElement). TryGetRoutingSlip applies the
            // shared MessageContext materializer to the deserialized slip's Attachments, so a consumer
            // that stored a string/int reads it straight back without a JsonElement cast failure.
            roundTrippedSlip.Attachments.Should().ContainKey("attachment-key");
            roundTrippedSlip.Attachments["attachment-key"].Should().BeOfType<string>().Which.Should().Be("attachment-value");

            roundTrippedSlip.Attachments.Should().ContainKey("attachment-number");
            // Numbers materialize to long (Int64) per the materializer's Newtonsoft-parity semantics.
            roundTrippedSlip.Attachments["attachment-number"].Should().BeOfType<long>().Which.Should().Be(42L);
        }

        // STRUCTURED + TYPED ATTACHMENTS FIDELITY: the "all areas" mandate for the RoutingSlip seam.
        // Newtonsoft's untyped read produced navigable JObject/JArray and CLR-typed leaves for object/
        // array Attachments values, so a consumer storing a Dictionary, an array, a DateTime, a string,
        // and an int could read them back as navigable structures and CLR types. After the STJ port the
        // global MaterializingObjectConverter on ChatterJson.Options materializes each Attachments value
        // INLINE during the RoutingSlip deserialize (no per-seam materialize line), so this asserts each
        // entry reads back as the correct CLR type / navigable structure through the real production
        // seams (WithRoutingSlip serialize -> TryGetRoutingSlip deserialize).
        [Fact]
        public void MustRoundTripStructuredAndTypedAttachmentsThroughProductionSeams()
        {
            var id = Guid.NewGuid();
            var instant = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);

            var slip = RoutingSlipBuilder.NewRoutingSlip(id)
                .WithRoute("only")
                .Build();

            // A spread of Attachments value shapes: primitive string, primitive int, a DateTime, a
            // structured object (Dictionary), and an array.
            slip.Attachments["str"] = "a-string";
            slip.Attachments["num"] = 42;
            slip.Attachments["when"] = instant;
            slip.Attachments["obj"] = new Dictionary<string, object> { ["id"] = 1, ["name"] = "abc" };
            slip.Attachments["arr"] = new object[] { 1, "two", true };

            // SERIALIZE seam.
            var serializeContext = new Dictionary<string, object>();
            var serializeSut = CreateContext(serializeContext);
            serializeSut.BrokeredMessage.WithRoutingSlip(slip);

            var serializedSlip = serializeContext[MessageContext.RoutingSlip];

            // DESERIALIZE seam.
            var deserializeContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = serializedSlip
            };
            var deserializeSut = CreateContext(deserializeContext);

            var found = deserializeSut.TryGetRoutingSlip(out var roundTrippedSlip);

            found.Should().BeTrue();
            roundTrippedSlip.Id.Should().Be(id);

            var attachments = roundTrippedSlip.Attachments;

            // string -> string
            attachments["str"].Should().BeOfType<string>().And.Be("a-string");

            // int -> long (untyped Newtonsoft-parity widening)
            attachments["num"].Should().BeOfType<long>().And.Be(42L);

            // strict ISO-8601 -> DateTime
            attachments["when"].Should().BeOfType<DateTime>();
            ((DateTime)attachments["when"]).ToUniversalTime().Should().Be(instant);

            // structured object -> navigable Dictionary<string, object> with materialized leaves
            var obj = attachments["obj"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            obj["id"].Should().BeOfType<long>().And.Be(1L);
            obj["name"].Should().BeOfType<string>().And.Be("abc");

            // array -> navigable List<object> with materialized elements
            var arr = attachments["arr"].Should().BeAssignableTo<IList<object>>().Subject;
            arr.Should().HaveCount(3);
            arr[0].Should().BeOfType<long>().And.Be(1L);
            arr[1].Should().BeOfType<string>().And.Be("two");
            arr[2].Should().BeOfType<bool>().And.Be(true);
        }
    }
}
