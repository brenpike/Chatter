using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingInboundBrokeredMessageFactory
{
    // Unit tests over the pure, I/O-free InboundBrokeredMessageFactory extracted from
    // ServiceBusReceiver.ReceiveMessageAsync: body-converter fallback, each of the four header
    // stamps, and the null-message guard. The header stamps are written into the context's MESSAGE
    // CONTEXT dictionary (ServiceBusReceivedMessage.ApplicationProperties is read-only), so they are
    // asserted via BrokeredMessage.MessageContext rather than back on the received message.
    public class WhenCreatingContext : Testing.Core.Context
    {
        private static InboundBrokeredMessageFactory CreateSut(IBodyConverterFactory bodyConverterFactory = null)
        {
            var logger = new Mock<ILogger>();
            if (bodyConverterFactory is null)
            {
                // Production IBodyConverterFactory never returns null; the default test factory
                // mirrors that by returning a real JsonBodyConverter.
                var defaultFactory = new Mock<IBodyConverterFactory>();
                defaultFactory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
                bodyConverterFactory = defaultFactory.Object;
            }

            return new InboundBrokeredMessageFactory(bodyConverterFactory, logger.Object);
        }

        [Fact]
        public void MustReturnNullWhenMessageNull()
        {
            var sut = CreateSut();
            sut.CreateContext(null, "receiver", CancellationToken.None).Should().BeNull();
        }

        [Fact]
        public void MustUseConverterReturnedByFactory()
        {
            // The InboundBrokeredMessage ctor stamps the chosen converter's ContentType into the
            // message context; a custom (non-JSON) ContentType is observable proof the factory's
            // converter was selected rather than the JsonBodyConverter default.
            var converter = new FakeBodyConverter("application/custom");
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(converter);
            var sut = CreateSut(factory.Object);

            var context = sut.CreateContext(ServiceBusMessageFactory.ReceivedMessage(), "receiver", CancellationToken.None);

            context.Should().NotBeNull();
            context.BrokeredMessage.MessageContext[MessageContext.ContentType].Should().Be("application/custom");
        }

        [Fact]
        public void MustFallBackToJsonConverterWhenFactoryThrows()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Throws(new InvalidOperationException("boom"));
            var sut = CreateSut(factory.Object);

            var context = sut.CreateContext(ServiceBusMessageFactory.ReceivedMessage(), "receiver", CancellationToken.None);

            context.Should().NotBeNull();
            context.BrokeredMessage.MessageContext[MessageContext.ContentType].Should().Be(new JsonBodyConverter().ContentType);
        }

        [Fact]
        public void MustStampTimeToLiveHeader()
        {
            var sut = CreateSut();
            var message = ServiceBusMessageFactory.ReceivedMessage();
            var context = sut.CreateContext(message, "receiver", CancellationToken.None);
            context.BrokeredMessage.MessageContext.Should().ContainKey(MessageContext.TimeToLive);
        }

        [Fact]
        public void MustStampExpiryTimeUtcHeader()
        {
            var sut = CreateSut();
            var message = ServiceBusMessageFactory.ReceivedMessage();
            var context = sut.CreateContext(message, "receiver", CancellationToken.None);
            context.BrokeredMessage.MessageContext.Should().ContainKey(MessageContext.ExpiryTimeUtc);
        }

        [Fact]
        public void MustStampInfrastructureTypeHeader()
        {
            var sut = CreateSut();
            var message = ServiceBusMessageFactory.ReceivedMessage();
            var context = sut.CreateContext(message, "receiver", CancellationToken.None);
            context.BrokeredMessage.MessageContext[MessageContext.InfrastructureType].Should().Be(ASBMessageContext.InfrastructureType);
        }

        [Fact]
        public void MustStampReceiveAttemptsFromDeliveryCount()
        {
            var sut = CreateSut();
            var message = ServiceBusMessageFactory.ReceivedMessage(deliveryCount: 4);
            var context = sut.CreateContext(message, "receiver", CancellationToken.None);
            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(4);
        }

        [Fact]
        public void MustIncludeMessageInReturnedContextContainer()
        {
            var sut = CreateSut();
            var message = ServiceBusMessageFactory.ReceivedMessage();
            var context = sut.CreateContext(message, "receiver", CancellationToken.None);
            context.Container.TryGet<Azure.Messaging.ServiceBus.ServiceBusReceivedMessage>(out var contained).Should().BeTrue();
            contained.Should().BeSameAs(message);
        }

        private class FakeBodyConverter : IBrokeredMessageBodyConverter
        {
            public FakeBodyConverter(string contentType) => ContentType = contentType;
            public string ContentType { get; }
            public TBody Convert<TBody>(byte[] body) => default;
            public byte[] Convert(object body) => System.Array.Empty<byte>();
            public string Stringify(byte[] body) => string.Empty;
            public string Stringify(object body) => string.Empty;
            public byte[] GetBytes(string body) => System.Array.Empty<byte>();
        }
    }
}
