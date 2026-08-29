using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingOutboundBrokeredMessageExtensions
{
    // Pins the claim ADR-0010 makes under "Propagation scope": W3C trace context survives an Azure Service Bus
    // round trip WITHOUT any adapter production change, because the whole MessageContext dictionary is projected
    // onto ServiceBusMessage.ApplicationProperties on send (MessageExtensions.WithApplicationProperties) and every
    // application property is copied back into the message context on receive (InboundBrokeredMessageFactory).
    // "traceparent"/"tracestate" are therefore ordinary application properties -- they are declared OUTSIDE
    // MessageContext (ADR-0010 D5) and no code in this adapter names them.
    //
    // These are unit tests over the pure mapping only; the live-broker proof (and the Azure SDK interop
    // consequence) lives in Integration/AzureServiceBusTraceContextInteropTests.
    public class WhenMappingTraceContextToApplicationProperties : Testing.Core.Context
    {
        // A well-formed W3C traceparent: version 00, a 32-hex trace id, a 16-hex span id, sampled flags.
        private const string TraceParentValue = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        private const string TraceStateValue = "chatter=1,vendor=abc";

        private readonly byte[] _body = new byte[] { 1, 2, 3 };
        private readonly JsonBodyConverter _converter = new JsonBodyConverter();

        private OutboundBrokeredMessage CreateSut(IDictionary<string, object> context)
            => new OutboundBrokeredMessage("message-id", _body, context, "destination", _converter);

        private static Dictionary<string, object> TraceContext()
            => new Dictionary<string, object>
            {
                [TraceContextHeaders.TraceParent] = TraceParentValue,
                [TraceContextHeaders.TraceState] = TraceStateValue,
            };

        private static InboundBrokeredMessageFactory CreateInboundFactory()
        {
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();
            bodyConverterFactory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return new InboundBrokeredMessageFactory(bodyConverterFactory.Object, Mock.Of<ILogger>());
        }

        [Fact]
        public void MustMapTraceParentToApplicationProperties()
        {
            var message = CreateSut(TraceContext()).AsAzureServiceBusMessage();

            message.ApplicationProperties.Should().ContainKey(TraceContextHeaders.TraceParent);
            message.ApplicationProperties[TraceContextHeaders.TraceParent].Should().Be(TraceParentValue);
        }

        [Fact]
        public void MustMapTraceStateToApplicationProperties()
        {
            var message = CreateSut(TraceContext()).AsAzureServiceBusMessage();

            message.ApplicationProperties.Should().ContainKey(TraceContextHeaders.TraceState);
            message.ApplicationProperties[TraceContextHeaders.TraceState].Should().Be(TraceStateValue);
        }

        [Fact]
        public void MustNotWriteTraceContextWhenTheContextCarriesNone()
        {
            // Off must mean OFF on the wire (ADR-0010 R2): nothing in this adapter invents a trace-context
            // application property, so a message context without one produces a message without one.
            var message = CreateSut(new Dictionary<string, object>()).AsAzureServiceBusMessage();

            message.ApplicationProperties.Should().NotContainKey(TraceContextHeaders.TraceParent);
            message.ApplicationProperties.Should().NotContainKey(TraceContextHeaders.TraceState);
        }

        [Fact]
        public void MustReturnTraceContextFromInboundApplicationProperties()
        {
            var received = ReceivedMessageCarrying(TraceContext());

            var context = CreateInboundFactory().CreateContext(received, "receiver", CancellationToken.None);

            context.BrokeredMessage.MessageContext[TraceContextHeaders.TraceParent].Should().Be(TraceParentValue);
            context.BrokeredMessage.MessageContext[TraceContextHeaders.TraceState].Should().Be(TraceStateValue);
        }

        [Fact]
        public void MustPreserveTraceContextAsStringsAcrossAFullRoundTrip()
        {
            // Send-side mapping feeds the receive-side mapping directly, so this is the whole adapter round trip:
            // the values must come back as System.String, not as a broker-specific encoding, or
            // TraceContextPropagator's string/byte[] coercion would be doing work this adapter never needs.
            var sent = CreateSut(TraceContext()).AsAzureServiceBusMessage();
            var received = ReceivedMessageCarrying(sent.ApplicationProperties);

            var context = CreateInboundFactory().CreateContext(received, "receiver", CancellationToken.None);

            context.BrokeredMessage.MessageContext[TraceContextHeaders.TraceParent].Should().BeOfType<string>().And.Be(TraceParentValue);
            context.BrokeredMessage.MessageContext[TraceContextHeaders.TraceState].Should().BeOfType<string>().And.Be(TraceStateValue);
        }

        [Fact]
        public void MustYieldAnExtractableRemoteContextAfterAFullRoundTrip()
        {
            // The round trip is only worth anything if the receive side can rebuild the producer's context from it:
            // this is the shape BrokerDiagnostics.StartReceive parents its span to.
            var sent = CreateSut(TraceContext()).AsAzureServiceBusMessage();
            var received = ReceivedMessageCarrying(sent.ApplicationProperties);

            var context = CreateInboundFactory().CreateContext(received, "receiver", CancellationToken.None);

            TraceContextPropagator.TryExtract(context.BrokeredMessage.MessageContext, out var producerContext).Should().BeTrue();
            producerContext.TraceId.ToHexString().Should().Be("0af7651916cd43dd8448eb211c80319c");
            producerContext.SpanId.ToHexString().Should().Be("b7ad6b7169203331");
            producerContext.TraceState.Should().Be(TraceStateValue);
            producerContext.IsRemote.Should().BeTrue();
        }

        // The SDK's own model factory builds a message in the RECEIVED state; ServiceBusReceivedMessage's
        // ApplicationProperties are read-only, so they can only be supplied here.
        private static ServiceBusReceivedMessage ReceivedMessageCarrying(IDictionary<string, object> applicationProperties)
            => ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: new BinaryData(new byte[] { 1 }),
                messageId: "message-id",
                contentType: "application/json",
                timeToLive: TimeSpan.FromMinutes(5),
                properties: applicationProperties,
                deliveryCount: 1,
                lockTokenGuid: Guid.NewGuid(),
                enqueuedTime: DateTimeOffset.UtcNow);
    }
}
