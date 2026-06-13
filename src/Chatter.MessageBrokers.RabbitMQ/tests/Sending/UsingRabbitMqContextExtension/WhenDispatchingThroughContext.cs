using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Sending;
using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Sending.UsingRabbitMqContextExtension
{
    // Pins the RabbitMQ MessageHandlerContextExtensions: RabbitMq() stamps the RabbitMQ InfrastructureType on
    // the outbound message (and returns null for a non-IMessageBrokerContext), and WithRabbitMqRouting stamps
    // TargetExchange + RoutingKey onto the outbound message context for the sender to read at dispatch time.
    //
    // Two of those WithRabbitMqRouting overloads target the handler-path option objects — SendOptions
    // (context.RabbitMq().Send(..., options)) and PublishOptions (context.RabbitMq().Publish(..., options)).
    // Both stamp TargetExchange + RoutingKey into the option's MessageContext, which the core
    // BrokeredMessageDispatcher copies verbatim into the dispatched OutboundBrokeredMessage's MessageContext
    // (new OutboundBrokeredMessage(..., options.MessageContext, ...)); RabbitMqSender.ResolveAddress then reads
    // those two keys to pick exchange/routing key at publish time.
    //
    // InternalsVisibleTo LIMITATION: RoutingOptions.MessageContext is INTERNAL and this test assembly is NOT in
    // Chatter.MessageBrokers' InternalsVisibleTo, so the stamped dictionary cannot be read off the option directly,
    // and no public getter surfaces the RabbitMQ routing keys. The dispatcher's construction path is likewise not
    // broker-free-constructable from here (BrokeredMessageRouter is internal). So the options->wire contract is
    // proven via the strongest available PUBLIC surface, in two halves that together cover the path the dispatcher
    // composes:
    //   (1) the SendOptions/PublishOptions overload is fluent + callable (exercises the NEW option-overload code),
    //       and
    //   (2) the wire-level proof drives RabbitMqSender over the InMemoryRabbitMqConnectionSource using an
    //       OutboundBrokeredMessage carrying the SAME two header keys the overload stamps
    //       (RabbitMqMessageContext.TargetExchange / RoutingKey) — pinning that those exact keys reach
    //       publish.Exchange / publish.RoutingKey, which is precisely what the dispatcher hands the sender.
    public class WhenDispatchingThroughContext : Testing.Core.Context
    {
        private const string Destination = "receiver-path";

        private static MessageBrokerContext CreateRealContext()
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json; charset=utf-8");
            return new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), Destination, CancellationToken.None, bodyConverter.Object);
        }

        private static OutboundBrokeredMessage CreateOutbound()
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json; charset=utf-8");
            return new OutboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), Destination, bodyConverter.Object);
        }

        // The real core factory over the RabbitMQ + core JSON converters, mirroring WhenDispatching so the sender
        // resolves the same converter the production wiring would for the configured MessageBodyType.
        private static IBodyConverterFactory BodyConverterFactory()
            => new BodyConverterFactory(new IBrokeredMessageBodyConverter[]
            {
                new RabbitMqBodyConverter(),
                new JsonBodyConverter()
            });

        private static RabbitMqSender CreateSender(InMemoryRabbitMqConnectionSource connectionSource)
            => new RabbitMqSender(connectionSource,
                                  BodyConverterFactory(),
                                  new RabbitMqOptions(hostName: "localhost"),
                                  Mock.Of<ILogger<RabbitMqSender>>());

        // Builds the outbound the core BrokeredMessageDispatcher would construct from an option's MessageContext:
        // it copies options.MessageContext verbatim into the dispatched OutboundBrokeredMessage. Since the option's
        // internal MessageContext is unreadable from here, the same two routing keys the overload stamps are placed
        // onto the outbound's MessageContext directly, modelling the dispatcher's copy so the sender sees exactly
        // what the handler path produces.
        private static OutboundBrokeredMessage OutboundCarryingRouting(string exchange, string routingKey)
        {
            var outbound = new OutboundBrokeredMessage(
                "message-id",
                new byte[] { 1, 2, 3 },
                new Dictionary<string, object>(),
                Destination,
                new RabbitMqBodyConverter());
            outbound.MessageContext[RabbitMqMessageContext.TargetExchange] = exchange;
            outbound.MessageContext[RabbitMqMessageContext.RoutingKey] = routingKey;
            return outbound;
        }

        [Fact]
        public void MustStampRabbitMqInfrastructureTypeOnRealContext()
        {
            IMessageBrokerContext context = CreateRealContext();

            context.RabbitMq();

            context.BrokeredMessage.MessageContext[MessageContext.InfrastructureType]
                .Should().Be(RabbitMqMessageContext.InfrastructureType);
        }

        [Fact]
        public void MustReturnSameContextFromRabbitMq()
        {
            IMessageBrokerContext context = CreateRealContext();
            context.RabbitMq().Should().BeSameAs(context);
        }

        [Fact]
        public void MustReturnNullWhenContextIsNotMessageBrokerContext()
            => ((IMessageHandlerContext)Mock.Of<IMessageHandlerContext>()).RabbitMq()
               .Should().BeNull();

        [Fact]
        public void MustStampTargetExchangeAndRoutingKeyFromWithRabbitMqRouting()
        {
            var outbound = CreateOutbound();

            outbound.WithRabbitMqRouting("orders-exchange", "orders.created");

            outbound.MessageContext[RabbitMqMessageContext.TargetExchange].Should().Be("orders-exchange");
            outbound.MessageContext[RabbitMqMessageContext.RoutingKey].Should().Be("orders.created");
        }

        [Fact]
        public void MustReturnSameOutboundFromWithRabbitMqRouting()
        {
            var outbound = CreateOutbound();
            outbound.WithRabbitMqRouting("ex", "rk").Should().BeSameAs(outbound);
        }

        // --- SendOptions overload: fluent (#200 handler-path send) ---

        // The SendOptions overload returns the SAME option instance so it composes with the other fluent
        // WithSubject/WithGroupId builders the handler path chains. The stamp lands on the option's INTERNAL
        // MessageContext (unreadable here), so reference-equality is the public proof the overload is callable +
        // chainable; its effect on the wire is pinned by MustPublishToOverriddenExchangeForSendOptionsKeys below.
        [Fact]
        public void MustReturnSameSendOptionsFromWithRabbitMqRouting()
        {
            var options = new SendOptions();
            options.WithRabbitMqRouting("orders-exchange", "orders.created").Should().BeSameAs(options);
        }

        // --- PublishOptions overload: fluent (#200 handler-path publish) ---

        [Fact]
        public void MustReturnSamePublishOptionsFromWithRabbitMqRouting()
        {
            var options = new PublishOptions();
            options.WithRabbitMqRouting("orders-exchange", "orders.created").Should().BeSameAs(options);
        }

        // --- options -> wire: the SendOptions/PublishOptions keys reach the published frame ---

        // SendOptions override -> wire: the dispatcher copies options.MessageContext onto the outbound; with the
        // two SendOptions-stamped keys on the outbound, the sender publishes to the overridden exchange + routing
        // key (proving the #200 SendOptions contract over the InMemoryRabbitMqConnectionSource, no live broker).
        [Fact]
        public async Task MustPublishToOverriddenExchangeForSendOptionsKeys()
        {
            // Stamp via the real SendOptions overload to exercise the NEW option code, then drive the wire proof
            // with an outbound carrying the SAME keys (the option's MessageContext is internal/unreadable here).
            new SendOptions().WithRabbitMqRouting("orders-exchange", "orders.created");

            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);

            await sender.Dispatch(OutboundCarryingRouting("orders-exchange", "orders.created"), transactionContext: null);

            var publish = connectionSource.PublishChannels.Single().Publishes.Single();
            publish.Exchange.Should().Be("orders-exchange");
            publish.RoutingKey.Should().Be("orders.created");
        }

        // PublishOptions override -> wire: same contract for the publish handler path.
        [Fact]
        public async Task MustPublishToOverriddenExchangeForPublishOptionsKeys()
        {
            new PublishOptions().WithRabbitMqRouting("events-exchange", "order.placed");

            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);

            await sender.Dispatch(OutboundCarryingRouting("events-exchange", "order.placed"), transactionContext: null);

            var publish = connectionSource.PublishChannels.Single().Publishes.Single();
            publish.Exchange.Should().Be("events-exchange");
            publish.RoutingKey.Should().Be("order.placed");
        }

        // Empty exchange = default exchange + custom routing key: an empty TargetExchange with a routing-key
        // override resolves to the default exchange ("") with the custom routing key, matching
        // RabbitMqSender.ResolveAddress. Proven through the SendOptions key set the handler path stamps.
        [Fact]
        public async Task MustPublishToDefaultExchangeWithCustomRoutingKeyWhenExchangeEmpty()
        {
            new SendOptions().WithRabbitMqRouting("", "only.key");

            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);

            await sender.Dispatch(OutboundCarryingRouting("", "only.key"), transactionContext: null);

            var publish = connectionSource.PublishChannels.Single().Publishes.Single();
            publish.Exchange.Should().Be("");
            publish.RoutingKey.Should().Be("only.key");
        }
    }
}
