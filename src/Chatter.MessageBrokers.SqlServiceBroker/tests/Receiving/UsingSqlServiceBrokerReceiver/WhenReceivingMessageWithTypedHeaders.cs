using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingSqlServiceBrokerReceiver
{
    // Pins the SSB receive-seam type-fidelity contract: SqlServiceBrokerReceiver.ReceiveMessageAsync
    // deserializes the ChatterBrokeredMessage envelope via JsonUnicodeBodyConverter, which reads through
    // ChatterJson.Deserialize, where the global MaterializingObjectConverter registered on ChatterJson.Options
    // restores OutboundBrokeredMessage.MessageContext's object-typed values to CLR types inline. The receiver
    // then only null-guards that context (the "no per-seam materialization needed" comment in
    // ReceiveMessageAsync) and never calls MessageContext.MaterializePersistedContext, so an upstream-stamped
    // NON-STRING header (e.g. a numeric ReceiveAttempts from a prior SSB hop) is found by the downstream
    // kind-tested GetMessageContextByKey<T> reads rather than read as absent on the live receive path only
    // because that global converter materialized it.
    //
    // -----------------------------------------------------------------------------------------------
    // REACHABLE-vs-DEFERRED LEDGER (mirrors the WhenDispatching ledger style)
    // -----------------------------------------------------------------------------------------------
    // DEFERRED (live-broker-only): SqlServiceBrokerReceiver.ReceiveMessageAsync opens a real SqlConnection
    //   via ISqlConnectionSource.OpenAsync and issues a RECEIVE against a Service Broker queue before any
    //   envelope is materialized. There is no in-memory IMessagingInfrastructureReceiver double, and the
    //   connection/transaction/RECEIVE plumbing cannot be exercised without a live broker. The end-to-end
    //   "RECEIVE -> classify -> materialize -> stamp" walk is therefore deferred to integration coverage.
    //
    // REACHABLE (pinned here): the unit-reachable boundary is the exact transformation the receiver applies
    //   in SqlServiceBrokerReceiver.ReceiveMessageAsync — deserialize the envelope body to
    //   OutboundBrokeredMessage via JsonUnicodeBodyConverter, then take brokeredMessage.MessageContext as
    //   the headers behind only a null-guard. We reproduce that seam exactly (same body converter, no
    //   explicit materialization call) and assert the deserialized headers expose a non-string value as its
    //   CLR type such that a downstream GetMessageContextByKey<long>/<string> read finds it rather than
    //   reading it as absent. The only materialization these facts observe is the global converter's. This
    //   is the regression gate; it does NOT fake the live RECEIVE.
    // -----------------------------------------------------------------------------------------------
    public class WhenReceivingMessageWithTypedHeaders : Testing.Core.Context
    {
        private const string Destination = "receiver-path";

        // Builds the on-the-wire envelope exactly as a sender would and as the receiver deserializes it:
        // an OutboundBrokeredMessage serialized via JsonUnicodeBodyConverter (System.Text.Json). The
        // deserialize runs through the global MaterializingObjectConverter, so the returned envelope's
        // MessageContext is exactly the headers the receiver hands downstream after its null-guard.
        private static OutboundBrokeredMessage DeserializeEnvelopeAsReceiverDoes(IDictionary<string, object> messageContext)
        {
            var bodyConverter = new JsonUnicodeBodyConverter();
            var envelope = new OutboundBrokeredMessage(
                messageId: "envelope-message-id",
                body: new byte[] { 1, 2, 3 },
                messageContext: messageContext,
                destination: Destination,
                bodyConverter: bodyConverter);

            byte[] wire = bodyConverter.Convert(envelope);
            return bodyConverter.Convert<OutboundBrokeredMessage>(wire);
        }

        // INVARIANT: a numeric header stamped upstream (e.g. ReceiveAttempts from a prior SSB hop) comes out
        // of the STJ envelope deserialize as a boxed long, and a downstream kind-tested
        // GetMessageContextByKey<long> read finds it rather than reading it as absent — the live-receive
        // regression gate. Oracle: this fact; removing the MaterializingObjectConverter registration from
        // ChatterJson.Options leaves the header a JsonElement and reddens it.
        [Fact]
        public void MustExposeNumericHeaderAsClrTypeSoTypedReadSucceeds()
        {
            var sentContext = new Dictionary<string, object>
            {
                [MessageContext.ReceiveAttempts] = 4,
                [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType,
            };

            IDictionary<string, object> headers = DeserializeEnvelopeAsReceiverDoes(sentContext).MessageContext;

            // Reconstruct the inbound message the way the receiver does (MessageBrokerContext is fed the
            // deserialized headers) and assert the downstream kind-tested reads FIND the materialized values.
            var inbound = new OutboundBrokeredMessage(
                "inbound-id",
                new byte[] { 1 },
                headers,
                Destination,
                new JsonUnicodeBodyConverter());

            inbound.GetMessageContextByKey<long>(MessageContext.ReceiveAttempts).Should().Be(4L,
                "a numeric ReceiveAttempts header from a prior hop must materialize to a CLR long so the kind-tested read finds it");
            inbound.GetMessageContextByKey<string>(MessageContext.InfrastructureType)
                   .Should().Be(SSBMessageContext.InfrastructureType,
                       "a string InfrastructureType header must remain a CLR string");
        }

        // INVARIANT: the deserialized numeric header is a boxed long (Newtonsoft parity), and the
        // OutboundBrokeredMessage.ReceiveAttempts accessor's Convert.ToInt32 tolerates it — pinning that
        // the receive seam yields the type the production accessor expects. Oracle: this fact; removing the
        // MaterializingObjectConverter registration from ChatterJson.Options leaves the header a JsonElement
        // and reddens it.
        [Fact]
        public void MustExposeReceiveAttemptsReadableAsInt()
        {
            var sentContext = new Dictionary<string, object>
            {
                [MessageContext.ReceiveAttempts] = 7,
            };

            IDictionary<string, object> headers = DeserializeEnvelopeAsReceiverDoes(sentContext).MessageContext;

            headers[MessageContext.ReceiveAttempts].Should().BeOfType<long>(
                "a JSON integer must materialize to a boxed long, matching Newtonsoft's untyped read");

            var inbound = new OutboundBrokeredMessage(
                "inbound-id",
                new byte[] { 1 },
                headers,
                Destination,
                new JsonUnicodeBodyConverter());

            inbound.ReceiveAttempts.Should().Be(7,
                "the production ReceiveAttempts accessor must read the deserialized boxed long as an int");
        }

        // STRUCTURED + TYPED ENVELOPE HEADER FIDELITY: the "all areas" mandate for the SSB receive seam.
        // An envelope header carrying a STRUCTURED (object and array) value, plus a typed primitive, must
        // survive the JsonUnicodeBodyConverter (UTF-16) envelope deserialize as navigable CLR collections /
        // CLR types — the same global MaterializingObjectConverter on the shared ChatterJson.Options drives
        // the materialization as for UTF-8 bodies. This pins that a prior-hop structured header (e.g. a
        // serialized sub-context) is readable as a navigable Dictionary/List downstream rather than a raw
        // JsonElement. Removing that converter's registration from ChatterJson.Options reddens this fact.
        [Fact]
        public void MustMaterializeStructuredAndTypedEnvelopeHeadersToClrTypes()
        {
            var sentContext = new Dictionary<string, object>
            {
                [MessageContext.ReceiveAttempts] = 2,
                ["structured-object"] = new Dictionary<string, object> { ["id"] = 1, ["name"] = "abc" },
                ["structured-array"] = new object[] { 1, "two", true },
            };

            // The receiver hands this context downstream behind only a null-guard.
            IDictionary<string, object> headers = DeserializeEnvelopeAsReceiverDoes(sentContext).MessageContext;

            // typed primitive -> long
            headers[MessageContext.ReceiveAttempts].Should().BeOfType<long>().And.Be(2L);

            // structured object header -> navigable Dictionary with materialized leaves
            var structuredObject = headers["structured-object"]
                .Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            structuredObject["id"].Should().BeOfType<long>().And.Be(1L);
            structuredObject["name"].Should().BeOfType<string>().And.Be("abc");

            // structured array header -> navigable List with materialized elements
            var structuredArray = headers["structured-array"]
                .Should().BeAssignableTo<IList<object>>().Subject;
            structuredArray.Should().HaveCount(3);
            structuredArray[0].Should().BeOfType<long>().And.Be(1L);
            structuredArray[1].Should().BeOfType<string>().And.Be("two");
            structuredArray[2].Should().BeOfType<bool>().And.Be(true);
        }
    }
}
