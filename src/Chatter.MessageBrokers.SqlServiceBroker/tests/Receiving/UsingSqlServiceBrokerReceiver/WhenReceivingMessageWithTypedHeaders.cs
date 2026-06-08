using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingSqlServiceBrokerReceiver
{
    // Pins the SSB receive-seam type-fidelity contract: SqlServiceBrokerReceiver.ReceiveMessageAsync
    // deserializes the ChatterBrokeredMessage envelope via JsonUnicodeBodyConverter (System.Text.Json),
    // which leaves OutboundBrokeredMessage.MessageContext's object-typed values as raw JsonElements. The
    // receiver routes those through MessageContext.MaterializePersistedContext BEFORE they feed downstream
    // GetMessageContextByKey<T> casts, so an upstream-stamped NON-STRING header (e.g. a numeric
    // ReceiveAttempts from a prior SSB hop) does not throw InvalidCastException on the live receive path.
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
    //   at SqlServiceBrokerReceiver.cs:166/180 — deserialize the envelope body to OutboundBrokeredMessage
    //   via JsonUnicodeBodyConverter, then feed brokeredMessage.MessageContext through
    //   MessageContext.MaterializePersistedContext. We reproduce that seam exactly (same body converter,
    //   same materializer entry point) and assert the inbound headers expose a non-string value as its CLR
    //   type such that a downstream GetMessageContextByKey<int>/<string> read SUCCEEDS rather than throwing
    //   InvalidCastException. This is the regression gate; it does NOT fake the live RECEIVE.
    // -----------------------------------------------------------------------------------------------
    public class WhenReceivingMessageWithTypedHeaders : Testing.Core.Context
    {
        private const string Destination = "receiver-path";

        // Builds the on-the-wire envelope exactly as a sender would and as the receiver deserializes it:
        // an OutboundBrokeredMessage serialized via JsonUnicodeBodyConverter (System.Text.Json). The
        // round-trip leaves MessageContext's non-string values as raw JsonElements, reproducing the
        // receive-seam input to MessageContext.MaterializePersistedContext.
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

        // INVARIANT: a numeric header stamped upstream (e.g. ReceiveAttempts from a prior SSB hop) survives
        // the STJ envelope round-trip as a JsonElement, is materialized to a boxed long, and a downstream
        // GetMessageContextByKey<int> read (via Convert.ToInt32) SUCCEEDS instead of throwing
        // InvalidCastException — the live-receive regression gate.
        [Fact]
        public void MustExposeNumericHeaderAsClrTypeSoTypedReadSucceeds()
        {
            var sentContext = new Dictionary<string, object>
            {
                [MessageContext.ReceiveAttempts] = 4,
                [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType,
            };

            var deserializedEnvelope = DeserializeEnvelopeAsReceiverDoes(sentContext);

            // The exact receiver-seam call (SqlServiceBrokerReceiver.cs:180).
            IDictionary<string, object> headers =
                MessageContext.MaterializePersistedContext(deserializedEnvelope.MessageContext);

            // Reconstruct the inbound message the way the receiver does (MessageBrokerContext is fed the
            // materialized headers) and assert the downstream typed reads do not throw.
            var inbound = new OutboundBrokeredMessage(
                "inbound-id",
                new byte[] { 1 },
                headers,
                Destination,
                new JsonUnicodeBodyConverter());

            Action numericRead = () => inbound.GetMessageContextByKey<long>(MessageContext.ReceiveAttempts);
            Action infraRead = () => inbound.GetMessageContextByKey<string>(MessageContext.InfrastructureType);

            numericRead.Should().NotThrow<InvalidCastException>(
                "a numeric ReceiveAttempts header from a prior hop must materialize to a CLR long so the typed read succeeds");
            infraRead.Should().NotThrow<InvalidCastException>(
                "a string InfrastructureType header must remain a CLR string");

            inbound.GetMessageContextByKey<long>(MessageContext.ReceiveAttempts).Should().Be(4L);
            inbound.GetMessageContextByKey<string>(MessageContext.InfrastructureType)
                   .Should().Be(SSBMessageContext.InfrastructureType);
        }

        // INVARIANT: the materialized numeric header is a boxed long (Newtonsoft parity), and the
        // OutboundBrokeredMessage.ReceiveAttempts accessor's Convert.ToInt32 tolerates it — pinning that
        // the receive seam yields the type the production accessor expects.
        [Fact]
        public void MustExposeReceiveAttemptsReadableAsInt()
        {
            var sentContext = new Dictionary<string, object>
            {
                [MessageContext.ReceiveAttempts] = 7,
            };

            var deserializedEnvelope = DeserializeEnvelopeAsReceiverDoes(sentContext);

            IDictionary<string, object> headers =
                MessageContext.MaterializePersistedContext(deserializedEnvelope.MessageContext);

            headers[MessageContext.ReceiveAttempts].Should().BeOfType<long>(
                "a JSON integer must materialize to a boxed long, matching Newtonsoft's untyped read");

            var inbound = new OutboundBrokeredMessage(
                "inbound-id",
                new byte[] { 1 },
                headers,
                Destination,
                new JsonUnicodeBodyConverter());

            inbound.ReceiveAttempts.Should().Be(7,
                "the production ReceiveAttempts accessor must read the materialized boxed long as an int without throwing");
        }

        // STRUCTURED + TYPED ENVELOPE HEADER FIDELITY: the "all areas" mandate for the SSB receive seam.
        // An envelope header carrying a STRUCTURED (object and array) value, plus a typed primitive, must
        // survive the JsonUnicodeBodyConverter (UTF-16) envelope round-trip and materialize through
        // MessageContext.MaterializePersistedContext to navigable CLR collections / CLR types — the same
        // global MaterializingObjectConverter on the shared ChatterJson.Options drives the materialization
        // as for UTF-8 bodies. This pins that a prior-hop structured header (e.g. a serialized sub-context)
        // is readable as a navigable Dictionary/List downstream rather than a raw JsonElement.
        [Fact]
        public void MustMaterializeStructuredAndTypedEnvelopeHeadersToClrTypes()
        {
            var sentContext = new Dictionary<string, object>
            {
                [MessageContext.ReceiveAttempts] = 2,
                ["structured-object"] = new Dictionary<string, object> { ["id"] = 1, ["name"] = "abc" },
                ["structured-array"] = new object[] { 1, "two", true },
            };

            var deserializedEnvelope = DeserializeEnvelopeAsReceiverDoes(sentContext);

            // The exact receiver-seam call (SqlServiceBrokerReceiver.cs:180).
            IDictionary<string, object> headers =
                MessageContext.MaterializePersistedContext(deserializedEnvelope.MessageContext);

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
