using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Sending;
using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Sending.UsingRabbitMqSender
{
    // Pins RabbitMqSender.Dispatch through the InMemoryRabbitMqConnectionSource (no live broker): the publish
    // goes through a pooled rental, addressing follows the default-exchange convention unless overridden via
    // WithRabbitMqRouting, ContentType is set from the body converter, MessageContext headers are mapped onto
    // the published properties, mandatory:true is set, and a collection dispatch publishes each message.
    public class WhenDispatching : Testing.Core.Context
    {
        private const string Destination = "target-queue";

        // The real core factory over the RabbitMQ + core JSON converters, so the sender resolves the same converter
        // the production wiring would for the configured MessageBodyType.
        private static IBodyConverterFactory BodyConverterFactory()
            => new BodyConverterFactory(new IBrokeredMessageBodyConverter[]
            {
                new RabbitMqBodyConverter(),
                new JsonBodyConverter()
            });

        private static RabbitMqSender CreateSender(InMemoryRabbitMqConnectionSource connectionSource,
                                                   RabbitMqOptions options = null)
            => new RabbitMqSender(connectionSource,
                                  BodyConverterFactory(),
                                  options ?? new RabbitMqOptions(hostName: "localhost"),
                                  Mock.Of<ILogger<RabbitMqSender>>());

        private static OutboundBrokeredMessage Message(
            string destination = Destination,
            byte[] body = null,
            IDictionary<string, object> messageContext = null)
            => new OutboundBrokeredMessage(
                Guid.NewGuid().ToString(),
                body ?? new byte[] { 1, 2, 3 },
                messageContext ?? new Dictionary<string, object>(),
                destination,
                new RabbitMqBodyConverter());

        // --- constructor guards ---

        [Fact]
        public void MustThrowWhenConnectionSourceNull()
        {
            Action act = () => new RabbitMqSender(null, BodyConverterFactory(), new RabbitMqOptions(hostName: "localhost"), Mock.Of<ILogger<RabbitMqSender>>());
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenBodyConverterFactoryNull()
        {
            Action act = () => new RabbitMqSender(new InMemoryRabbitMqConnectionSource(), null, new RabbitMqOptions(hostName: "localhost"), Mock.Of<ILogger<RabbitMqSender>>());
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenOptionsNull()
        {
            Action act = () => new RabbitMqSender(new InMemoryRabbitMqConnectionSource(), BodyConverterFactory(), null, Mock.Of<ILogger<RabbitMqSender>>());
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenLoggerNull()
        {
            Action act = () => new RabbitMqSender(new InMemoryRabbitMqConnectionSource(), BodyConverterFactory(), new RabbitMqOptions(hostName: "localhost"), null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public async Task MustThrowWhenSingleMessageNull()
        {
            var sender = CreateSender(new InMemoryRabbitMqConnectionSource());
            Func<Task> act = () => sender.Dispatch((OutboundBrokeredMessage)null, transactionContext: null);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task MustThrowWhenCollectionNull()
        {
            var sender = CreateSender(new InMemoryRabbitMqConnectionSource());
            Func<Task> act = () => sender.Dispatch((IEnumerable<OutboundBrokeredMessage>)null, transactionContext: null);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        // --- default-exchange convention ---

        [Fact]
        public async Task MustPublishToDefaultExchangeWithDestinationAsRoutingKey()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);

            await sender.Dispatch(Message(), transactionContext: null);

            var publish = connectionSource.PublishChannels.Single().Publishes.Single();
            publish.Exchange.Should().BeEmpty();
            publish.RoutingKey.Should().Be(Destination);
        }

        [Fact]
        public async Task MustAcquireExactlyOnePublishChannelPerMessage()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);

            await sender.Dispatch(Message(), transactionContext: null);

            connectionSource.AcquirePublishChannelCount.Should().Be(1);
        }

        // --- WithRabbitMqRouting override ---

        [Fact]
        public async Task MustPublishToOverriddenExchangeAndRoutingKey()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);
            var message = Message();
            message.WithRabbitMqRouting("orders-exchange", "orders.created");

            await sender.Dispatch(message, transactionContext: null);

            var publish = connectionSource.PublishChannels.Single().Publishes.Single();
            publish.Exchange.Should().Be("orders-exchange");
            publish.RoutingKey.Should().Be("orders.created");
        }

        // --- properties: content type, mandatory, headers, body ---

        // The advertised ContentType comes from the converter the core factory resolves for the configured
        // MessageBodyType. The default ("application/json; charset=utf-8") resolves the RabbitMqBodyConverter, whose
        // ContentType the sender stamps — proving the option is no longer ignored.
        [Fact]
        public async Task MustSetContentTypeFromFactoryResolvedConverterForConfiguredBodyType()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource,
                new RabbitMqOptions(hostName: "localhost", messageBodyType: "application/json; charset=utf-8"));

            await sender.Dispatch(Message(), transactionContext: null);

            connectionSource.PublishChannels.Single().Publishes.Single()
                .ContentType.Should().Be("application/json; charset=utf-8");
        }

        // An UNKNOWN configured content type resolves the core JsonBodyConverter fallback, so the sender advertises
        // that fallback's ContentType ("application/json") rather than a hardwired string — the option now drives
        // the advertised type end to end.
        [Fact]
        public async Task MustAdvertiseFallbackContentTypeWhenConfiguredBodyTypeIsUnknown()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource,
                new RabbitMqOptions(hostName: "localhost", messageBodyType: "application/x-unknown"));

            await sender.Dispatch(Message(), transactionContext: null);

            connectionSource.PublishChannels.Single().Publishes.Single()
                .ContentType.Should().Be("application/json");
        }

        [Fact]
        public async Task MustPublishWithMandatoryTrue()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);

            await sender.Dispatch(Message(), transactionContext: null);

            connectionSource.PublishChannels.Single().Publishes.Single()
                .Mandatory.Should().BeTrue();
        }

        [Fact]
        public async Task MustMapMessageContextOntoPublishedHeaders()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);
            var context = new Dictionary<string, object> { ["custom-header"] = "header-value" };

            await sender.Dispatch(Message(messageContext: context), transactionContext: null);

            connectionSource.PublishChannels.Single().Publishes.Single()
                .Headers.Should().ContainKey("custom-header")
                .WhoseValue.Should().Be("header-value");
        }

        [Fact]
        public async Task MustPublishTheMessageBody()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);
            var body = new byte[] { 9, 8, 7, 6 };

            await sender.Dispatch(Message(body: body), transactionContext: null);

            connectionSource.PublishChannels.Single().Publishes.Single()
                .Body.Should().Equal(body);
        }

        // --- collection overload publishes each message ---

        [Fact]
        public async Task MustPublishEachMessageInCollection()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);

            await sender.Dispatch(new[] { Message("A"), Message("B") }, transactionContext: null);

            connectionSource.AcquirePublishChannelCount.Should().Be(2);
            connectionSource.PublishChannels
                .Select(channel => channel.Publishes.Single().RoutingKey)
                .Should().BeEquivalentTo(new[] { "A", "B" });
        }
    }
}
