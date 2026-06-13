using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Canonical end-to-end round-trip proof for the RabbitMQ integration harness. The SYSTEM UNDER TEST is
    // Chatter's own RabbitMQ send + receive path: a command dispatched ONLY through Chatter's
    // IBrokeredMessageDispatcher.Send (harness.SendToQueueAsync / SendWithRoutingAsync) is delivered by the
    // in-process receiver pump (ChatterRabbitMqPipelineHarness) to a Chatter-resolved
    // RecordingMessageHandler<TMessage>, and every assertion reads the payload Chatter deserialized and the
    // IMessageBrokerContext Chatter built — never a raw RabbitMQ.Client type. This exercises the
    // OutboundBrokeredMessage envelope round-trip through RabbitMQ and the inbound header stamps the
    // RabbitMqReceiver applies on receive (InfrastructureType, ReceiveAttempts).
    //
    // Two cases: (a) the default-exchange convention (exchange "" + routing key = queue name); (b) the
    // WithRabbitMqRouting override against an explicitly declared direct exchange + binding.
    //
    // The facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the nightly RabbitMQ CI lane (`--filter Category=Integration`) runs them for
    // real. Mirrors the SQL Service Broker SsbRoundTripTests analogue.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqRoundTripTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqRoundTripTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // A command carrying string/int/bool fields so the body round-trip exercises Chatter's STJ body
        // converter for each scalar kind (the on-receive materialization must restore the exact CLR values).
        public sealed class RoundTripCommand : ICommand
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public bool Flag { get; set; }
        }

        // Distinct command type for the exchange-routed case so its queue state is independent of the
        // default-exchange case in this collection.
        public sealed class RoutedRoundTripCommand : ICommand
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        // Singleton probe the marker converter reports through: the converter is registered scoped (a fresh
        // instance per receive scope), so the test cannot hold the instance — it observes selection through this
        // shared singleton, mirroring how RecordingMessageHandler reports through the HandlerSignalRegistry.
        public sealed class ContentTypeConverterSelection
        {
            private int _selected;

            public bool WasSelected => Volatile.Read(ref _selected) != 0;

            public void MarkSelected() => Interlocked.Exchange(ref _selected, 1);
        }

        // A marker IBrokeredMessageBodyConverter keyed under a custom content-type that records when it is invoked
        // and otherwise delegates to the same UTF-8 JSON encoding the production RabbitMqBodyConverter uses. The
        // core BodyConverterFactory keys converters by ContentType, so the receiver resolves THIS converter only
        // when the delivered per-message content-type matches — recording its Convert<TBody> invocation is the
        // GAP B observation that selection came from the delivered content-type.
        public sealed class MarkerContentTypeBodyConverter : IBrokeredMessageBodyConverter
        {
            private readonly ContentTypeConverterSelection _selection;

            public MarkerContentTypeBodyConverter(string contentType, ContentTypeConverterSelection selection)
            {
                ContentType = contentType;
                _selection = selection;
            }

            public string ContentType { get; }

            public TBody Convert<TBody>(byte[] body)
            {
                _selection.MarkSelected();
                return System.Text.Json.JsonSerializer.Deserialize<TBody>(Stringify(body), ChatterJson.Options);
            }

            public byte[] Convert(object body) => GetBytes(Stringify(body));

            public string Stringify(byte[] body) => System.Text.Encoding.UTF8.GetString(body);

            public string Stringify(object body)
                => System.Text.Json.JsonSerializer.Serialize(body, ChatterJson.Options);

            public byte[] GetBytes(string body) => System.Text.Encoding.UTF8.GetBytes(body);
        }

        // Default-exchange round-trip: a command sent through Chatter's dispatcher to the work-queue name is
        // delivered by Chatter's RabbitMQ pump to the registered handler with its string/int/bool payload
        // deserialized EXACTLY, and the inbound RabbitMqMessageContext stamps the receiver applies are present
        // and correct. Quorum queue so ReceiveAttempts is sourced from the native x-delivery-count (+1).
        [RequiresDockerFact]
        public async Task SentCommandRoundTripsToHandlerOverDefaultExchange()
        {
            var set = RabbitMqTopology.CreateSet("roundtrip", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<RoundTripCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(RoundTripCommand));
            try
            {
                await harness.StartAsync();

                var sent = new RoundTripCommand { Name = "rmq-round-trip", Count = 42, Flag = true };
                await harness.SendToQueueAsync(sent, set.WorkQueueName);

                var handled = await harness.WaitForHandledAsync<RoundTripCommand>(HandlerWait);

                // Payload round-trip: every scalar kind must survive the OutboundBrokeredMessage envelope
                // round-trip through RabbitMQ verbatim.
                handled.Message.Should().NotBeNull("Chatter's RabbitMQ pipeline must deliver the command to the handler");
                handled.Message.Name.Should().Be("rmq-round-trip");
                handled.Message.Count.Should().Be(42);
                handled.Message.Flag.Should().BeTrue();

                // Inbound broker context: the handler must receive an IMessageBrokerContext on the RabbitMQ
                // receive path, carrying the header stamps RabbitMqReceiver applies in ReceiveMessageAsync.
                handled.Context.Should().NotBeNull(
                    "the handler context must be an IMessageBrokerContext on the broker receive path");
                var inbound = handled.Context.BrokeredMessage;

                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(RabbitMqMessageContext.InfrastructureType);

                // ReceiveAttempts is sourced from the quorum native x-delivery-count + 1: at least one for a
                // first delivery.
                inbound.MessageContext.Should().ContainKey(MessageContext.ReceiveAttempts);
                Convert.ToInt32(inbound.MessageContext[MessageContext.ReceiveAttempts])
                    .Should().BeGreaterThanOrEqualTo(1, "a delivered message has been received at least once");

                harness.GetSignal<RoundTripCommand>().InvocationCount
                    .Should().Be(1, "the command must be delivered to the handler exactly once on the happy path");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // P1 + P2 REPRODUCTION against a REAL broker: a command sent WITH a TimeToLive and a custom string
        // application header must (a) publish without throwing — the TimeSpan TimeToLive is lifted onto the native
        // BasicProperties.Expiration rather than the field table, which the client cannot encode (P2) — and
        // (b) reach the handler with its auto-stamped string CorrelationId and the custom string header intact,
        // even though a real broker delivers both as AMQP longstr (byte[]); the inbound (string) cast would
        // otherwise throw InvalidCastException before the handler ran (P1). Both halves are proven through
        // Chatter's own send + receive path, not raw RabbitMQ.Client types.
        [RequiresDockerFact]
        public async Task SentCommandWithTimeToLiveAndStringHeaderRoundTripsToHandler()
        {
            const string customHeaderKey = "x-chatter-it-custom";
            const string customHeaderValue = "custom-round-trip-value";

            var set = RabbitMqTopology.CreateSet("ttlheader", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<RoundTripCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(RoundTripCommand));
            try
            {
                await harness.StartAsync();

                var sent = new RoundTripCommand { Name = "ttl-header-round-trip", Count = 9, Flag = false };
                // A long TTL so the message does not expire before the in-process pump delivers it; the point is
                // that the publish succeeds (TimeSpan lifted onto native Expiration) and the message still arrives.
                await harness.SendToQueueWithTimeToLiveAndHeaderAsync(
                    sent, set.WorkQueueName, TimeSpan.FromMinutes(5), customHeaderKey, customHeaderValue);

                var handled = await harness.WaitForHandledAsync<RoundTripCommand>(HandlerWait);

                handled.Message.Should().NotBeNull(
                    "a message published with a TimeToLive must not fault at publish and must round-trip to the handler");
                handled.Message.Name.Should().Be("ttl-header-round-trip");

                var inbound = handled.Context.BrokeredMessage;

                // P1: the auto-stamped string CorrelationId survives the longstr coercion and is surfaced as a CLR
                // string — the receive would otherwise have thrown InvalidCastException before the handler ran.
                inbound.CorrelationId.Should().NotBeNullOrEmpty(
                    "the string CorrelationId must decode back to a CLR string on the real broker round-trip");

                // The custom string header is an UNKNOWN key, so the marshaller preserves it verbatim; a real
                // broker delivers it as the longstr byte[] it was published as.
                inbound.MessageContext.Should().ContainKey(customHeaderKey);
                var customHeader = inbound.MessageContext[customHeaderKey];
                var customHeaderAsString = customHeader is byte[] bytes
                    ? System.Text.Encoding.UTF8.GetString(bytes)
                    : customHeader as string;
                customHeaderAsString.Should().Be(customHeaderValue,
                    "the custom string header must survive the round-trip (verbatim as a longstr byte[])");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // GAP B REPRODUCTION against a REAL broker: a command sent with an explicit per-message ContentType stamp
        // must round-trip so the RECEIVE side selects the body converter from the DELIVERED per-message
        // content-type (the native BasicProperties.ContentType the broker frame carried), NOT the converter
        // resolved for the configured MessageBodyType. A marker IBrokeredMessageBodyConverter keyed under the
        // custom content-type is registered alongside the default; the receiver must select it because the
        // delivery carried that content-type, and the handler must still deserialize the payload exactly. The
        // inbound MessageContext.ContentType must equal the delivered content-type so a downstream re-send carries
        // the same stamp. (The string CorrelationId dual-home survival of the real broker's longstr coercion is
        // already proven by SentCommandWithTimeToLiveAndStringHeaderRoundTripsToHandler above — not duplicated.)
        [RequiresDockerFact]
        public async Task SentCommandWithCustomContentTypeSelectsDeliveredConverterOnReceive()
        {
            const string customContentType = "application/vnd.chatter.it.roundtrip+json";

            var set = RabbitMqTopology.CreateSet("contenttype", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var converterSelection = new ContentTypeConverterSelection();

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<RoundTripCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                services =>
                {
                    // Record the singleton selection probe and register a marker body converter keyed under the
                    // custom content-type. The core BodyConverterFactory enumerates IBrokeredMessageBodyConverter
                    // and keys each by its ContentType, so the receiver resolves THIS converter only when the
                    // delivered per-message content-type matches — the GAP B proof.
                    services.AddSingleton(converterSelection);
                    services.AddScoped<IBrokeredMessageBodyConverter>(_ =>
                        new MarkerContentTypeBodyConverter(customContentType, converterSelection));
                },
                typeof(RoundTripCommand));
            try
            {
                await harness.StartAsync();

                var sent = new RoundTripCommand { Name = "content-type-round-trip", Count = 13, Flag = true };
                await harness.SendToQueueWithContentTypeAsync(sent, set.WorkQueueName, customContentType);

                var handled = await harness.WaitForHandledAsync<RoundTripCommand>(HandlerWait);

                // Payload must deserialize exactly through the DELIVERED-content-type converter.
                handled.Message.Should().NotBeNull(
                    "a message stamped with a custom content-type must still round-trip to the handler");
                handled.Message.Name.Should().Be("content-type-round-trip");
                handled.Message.Count.Should().Be(13);
                handled.Message.Flag.Should().BeTrue();

                var inbound = handled.Context.BrokeredMessage;

                // GAP B: the receiver selected the body converter keyed under the DELIVERED per-message
                // content-type — the marker converter recorded a Convert<TBody> invocation, proving selection came
                // from the delivered frame content-type and not the configured MessageBodyType.
                converterSelection.WasSelected.Should().BeTrue(
                    "the receiver must pick the body converter from the delivered per-message content-type (GAP B)");

                // The delivered content-type must surface into the inbound context so a downstream re-send carries
                // the same stamp.
                inbound.MessageContext.Should().ContainKey(MessageContext.ContentType);
                inbound.MessageContext[MessageContext.ContentType]
                    .Should().Be(customContentType,
                        "the delivered content-type must reconstitute onto the inbound MessageContext.ContentType");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // WithRabbitMqRouting round-trip: a command sent with an explicit direct exchange + routing key is
        // delivered by Chatter's pump to the work queue bound under that key, proving the sender's
        // ResolveAddress override path (exchange != "" / routing key != Destination) reaches the bound queue.
        [RequiresDockerFact]
        public async Task SentCommandRoundTripsToHandlerOverExplicitExchangeRouting()
        {
            const string exchange = "chatter_rmq_it_roundtrip_exchange";
            const string routingKey = "chatter.rmq.it.roundtrip.key";

            var set = RabbitMqTopology.CreateSet("routed", QueueType.Quorum, exchange: exchange, routingKey: routingKey);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<RoutedRoundTripCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(RoutedRoundTripCommand));
            try
            {
                await harness.StartAsync();

                var sent = new RoutedRoundTripCommand { Name = "rmq-routed", Count = 7 };
                // destinationPath is still the work-queue name (the dispatcher needs a Destination), but the
                // explicit exchange + routing key override addressing so the publish goes through the declared
                // exchange and binding rather than the default exchange.
                await harness.SendWithRoutingAsync(sent, set.WorkQueueName, exchange, routingKey);

                var handled = await harness.WaitForHandledAsync<RoutedRoundTripCommand>(HandlerWait);

                handled.Message.Should().NotBeNull(
                    "the explicit exchange + routing key must route the command through the declared binding to the work queue");
                handled.Message.Name.Should().Be("rmq-routed");
                handled.Message.Count.Should().Be(7);

                var inbound = handled.Context.BrokeredMessage;
                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(RabbitMqMessageContext.InfrastructureType);
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
