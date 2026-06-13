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
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
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

        // --- type-aware header marshalling (the single boundary) ---

        // P2 REPRODUCTION: OutboundBrokeredMessage.WithTimeToLive stamps MessageContext.TimeToLive as a TimeSpan,
        // which the RabbitMQ.Client 7.2.1 field table CANNOT encode — a raw copy of the context onto the header
        // table threw at publish and the native BasicProperties.Expiration was never set. The marshaller lifts
        // TimeToLive onto the native Expiration (ms string) and drops the key from the table, so the publish
        // succeeds and the TTL is carried natively.
        [Fact]
        public async Task MustLiftTimeToLiveOntoNativeExpirationAndDropHeaderKey()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);
            var message = Message();
            message.WithTimeToLive(TimeSpan.FromMilliseconds(1500));

            await sender.Dispatch(message, transactionContext: null);

            var publish = connectionSource.PublishChannels.Single().Publishes.Single();
            publish.Expiration.Should().Be("1500");
            publish.Headers.Should().NotContainKey(MessageContext.TimeToLive);
        }

        // A non-positive TimeToLive floors at "0" rather than emitting a negative expiration.
        [Fact]
        public async Task MustFloorNonPositiveTimeToLiveExpirationAtZero()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);
            var message = Message();
            message.WithTimeToLive(TimeSpan.Zero);

            await sender.Dispatch(message, transactionContext: null);

            var publish = connectionSource.PublishChannels.Single().Publishes.Single();
            publish.Expiration.Should().Be("0");
            publish.Headers.Should().NotContainKey(MessageContext.TimeToLive);
        }

        // A round-tripped ulong (e.g. a stale RabbitMqMessageContext.DeliveryTag carried in the context) is widened
        // to a table-legal long by the marshaller instead of faulting the publish, since the field table cannot
        // encode a ulong.
        [Fact]
        public async Task MustPublishWhenContextCarriesRoundTrippedUlongValue()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);
            var context = new Dictionary<string, object> { [RabbitMqMessageContext.DeliveryTag] = 7UL };

            Func<Task> act = () => sender.Dispatch(Message(messageContext: context), transactionContext: null);

            await act.Should().NotThrowAsync();
            var publish = connectionSource.PublishChannels.Single().Publishes.Single();
            publish.Headers[RabbitMqMessageContext.DeliveryTag].Should().Be(7L);
        }

        // Full boundary round-trip: a published header bag, coerced to the field table by ToHeaderTable, then
        // longstr-coerced to byte[] the way a real broker delivers it, then projected back through ToContext, must
        // restore the known string-typed keys (CorrelationId) AND custom unknown string keys to a CLR string —
        // proving the two halves of the single boundary compose.
        [Fact]
        public void MustRoundTripStringHeadersThroughBoundaryToClrString()
        {
            var properties = new BasicProperties();
            var context = new Dictionary<string, object>
            {
                [MessageContext.CorrelationId] = "corr-123",
                ["custom-string-header"] = "custom-value"
            };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, properties);

            // Model the broker's on-the-wire longstr coercion: a string header is delivered as a UTF-8 byte[].
            var onWire = new Dictionary<string, object>();
            foreach (var entry in table)
            {
                onWire[entry.Key] = entry.Value is string asString
                    ? System.Text.Encoding.UTF8.GetBytes(asString)
                    : entry.Value;
            }

            var roundTripped = RabbitMqHeaderMarshaller.ToContext(onWire);

            // The known string-typed key is decoded back to a CLR string.
            roundTripped[MessageContext.CorrelationId].Should().Be("corr-123");
            // The unknown string key is preserved verbatim as the byte[] the broker delivered (never force-decoded).
            roundTripped["custom-string-header"].Should().BeEquivalentTo(System.Text.Encoding.UTF8.GetBytes("custom-value"));
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

        // --- publish-fault propagation + permit conservation ---

        // Regression: pins that Dispatch does NOT swallow a faulted BasicPublishAsync. The seam faults
        // with PublishException(isReturn:true), which is the exception RabbitMQ.Client 7.2.1 raises when
        // confirm-tracking correlates a basic.return (unroutable mandatory publish) to a publish-sequence-
        // number — so this test is constructable broker-free. The genuine broker fault-on-return path
        // (confirm-tracking → HandleReturn → HandleNack(isReturn:true) → tcs.SetException) is provable only
        // on the nightly Docker integration lane; this test pins the sender's non-swallow contract without
        // a live broker.
        [Fact]
        public async Task MustPropagateFaultFromPublishChannelForSingleMessage()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);
            // publishSequenceNumber 1, isReturn:true models a basic.return for an unroutable mandatory publish.
            var fault = new PublishException(publishSequenceNumber: 1, isReturn: true);

            // Configure the fault BEFORE Dispatch acquires the rental, so the channel is pre-seeded.
            // Dispatch calls AcquirePublishChannelAsync, which creates and records a new RecordingChannel;
            // set the fault on that channel by hooking into the source before dispatch.
            RecordingChannel publishChannel = null;
            connectionSource.OnPublishChannelCreated = channel => { publishChannel = channel; channel.PublishFault = fault; };

            Func<Task> act = () => sender.Dispatch(Message(), transactionContext: null);

            await act.Should().ThrowAsync<PublishException>();

            // The publish was still recorded before the fault was raised (seam records-then-faults).
            publishChannel.Publishes.Should().HaveCount(1);

            // Permit conservation: the rental's DisposeAsync (via await using) must have run, releasing the
            // pool semaphore permit even on the fault path. The RecordingChannel.Disposed flag confirms
            // ReturnPublishChannel disposed (not re-pooled) the channel, which also implies permit release.
            publishChannel.Disposed.Should().BeTrue("the rental must dispose the channel on the fault path");
        }

        // Regression: same non-swallow + permit-conservation contract for the IEnumerable<> overload,
        // which delegates to the single-message Dispatch in a foreach; the fault on the first message
        // must propagate out of the collection dispatch as well.
        [Fact]
        public async Task MustPropagateFaultFromPublishChannelForCollectionDispatch()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var sender = CreateSender(connectionSource);
            var fault = new PublishException(publishSequenceNumber: 1, isReturn: true);

            connectionSource.OnPublishChannelCreated = channel => channel.PublishFault = fault;

            Func<Task> act = () => sender.Dispatch(new[] { Message("A"), Message("B") }, transactionContext: null);

            await act.Should().ThrowAsync<PublishException>();

            // Only the first message's channel was acquired before the fault propagated.
            var faultedChannel = connectionSource.PublishChannels.First();
            faultedChannel.Publishes.Should().HaveCount(1);
            faultedChannel.Disposed.Should().BeTrue("rental must dispose the channel on the fault path");
        }
    }
}
