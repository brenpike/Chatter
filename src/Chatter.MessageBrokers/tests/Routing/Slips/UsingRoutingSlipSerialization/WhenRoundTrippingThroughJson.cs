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

            // KNOWN UNTESTED SUB-SURFACE: RoutingSlip.Attachments (IDictionary<string, object>) is left
            // empty here on purpose. Its object-boxing round-trip is serializer-dependent (Newtonsoft
            // boxes to JObject; STJ would box to JsonElement), so it is an STJ-changeable surface that is
            // intentionally NOT pinned by this characterization test.
        }
    }
}
