using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Sending;
using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Sending
{
    // Pins the single-pass consumption contract that IMessagingInfrastructureDispatcher.Dispatch documents: the
    // outbound sequence handed to a dispatcher is a lazy iterator whose body RE-RUNS per enumeration (destination
    // resolution, body conversion, message-id generation, trace-context injection, send-span batch counting), so a
    // second pass silently doubles that work and mis-counts the send span. RabbitMqSender is compliant today; these
    // tests exist so a capacity hint, a defensive materialisation, or a count-then-send refactor cannot reintroduce
    // the second pass unnoticed.
    public class WhenDispatchSequenceIsEnumerated : Testing.Core.Context
    {
        private const string ChannelAcquiredEntry = "publish-channel-acquired";

        private static RabbitMqSender CreateSender(InMemoryRabbitMqConnectionSource connectionSource)
            => new RabbitMqSender(connectionSource,
                                  new BodyConverterFactory(new IBrokeredMessageBodyConverter[]
                                  {
                                      new RabbitMqBodyConverter(),
                                      new JsonBodyConverter()
                                  }),
                                  new RabbitMqOptions(hostName: "localhost"),
                                  Mock.Of<ILogger<RabbitMqSender>>());

        private static OutboundBrokeredMessage Message(string destination)
            => new OutboundBrokeredMessage(Guid.NewGuid().ToString(),
                                           new byte[] { 1, 2, 3 },
                                           new Dictionary<string, object>(),
                                           destination,
                                           new RabbitMqBodyConverter());

        private static EnumerationProbeSequence<OutboundBrokeredMessage> Probe(List<string> pullTimeline, params string[] destinations)
            => new EnumerationProbeSequence<OutboundBrokeredMessage>(destinations.Select(Message).ToArray(), pullTimeline);

        private static int PublishCount(InMemoryRabbitMqConnectionSource connectionSource)
            => connectionSource.PublishChannels.Sum(channel => channel.Publishes.Count);

        [Fact]
        public async Task MustAskForExactlyOneEnumeratorPerDispatch()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var probe = Probe(new List<string>(), "A", "B", "C");

            await CreateSender(connectionSource).Dispatch(probe, transactionContext: null);

            probe.EnumeratorRequestCount.Should().Be(1);
        }

        [Fact]
        public async Task MustYieldEachOutboundMessageExactlyOnceAcrossThePass()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var probe = Probe(new List<string>(), "A", "B", "C");

            await CreateSender(connectionSource).Dispatch(probe, transactionContext: null);

            probe.YieldCountsPerPass.Should().Equal(new[] { 3 });
            probe.YieldedCount.Should().Be(probe.ItemCount);
        }

        [Fact]
        public async Task MustPublishExactlyOneMessagePerOutboundMessage()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var probe = Probe(new List<string>(), "A", "B", "C");

            await CreateSender(connectionSource).Dispatch(probe, transactionContext: null);

            PublishCount(connectionSource).Should().Be(3);
            connectionSource.PublishChannels
                            .SelectMany(channel => channel.Publishes)
                            .Select(publish => publish.RoutingKey)
                            .Should().BeEquivalentTo(new[] { "A", "B", "C" });
        }

        [Fact]
        public async Task MustPublishEachMessageAsItIsPulledRatherThanAfterWalkingTheSequence()
        {
            // Interleaving is the observable difference between streaming consumption and a walk-then-send shape: a
            // dispatcher that counted or materialised the sequence first would record every pull BEFORE the first
            // publish channel was acquired.
            var pullTimeline = new List<string>();
            var connectionSource = new InMemoryRabbitMqConnectionSource
            {
                OnPublishChannelCreated = _ => pullTimeline.Add(ChannelAcquiredEntry)
            };
            var probe = Probe(pullTimeline, "A", "B");

            await CreateSender(connectionSource).Dispatch(probe, transactionContext: null);

            pullTimeline.Should().Equal(new[]
            {
                EnumerationProbeSequence<OutboundBrokeredMessage>.EnumeratorRequestedEntry,
                EnumerationProbeSequence<OutboundBrokeredMessage>.YieldedEntryPrefix + "0",
                ChannelAcquiredEntry,
                EnumerationProbeSequence<OutboundBrokeredMessage>.YieldedEntryPrefix + "1",
                ChannelAcquiredEntry
            });
        }

        [Fact]
        public async Task MustAskForExactlyOneEnumeratorWhenSequenceIsEmpty()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var probe = Probe(new List<string>());

            await CreateSender(connectionSource).Dispatch(probe, transactionContext: null);

            probe.EnumeratorRequestCount.Should().Be(1);
            PublishCount(connectionSource).Should().Be(0);
        }
    }
}
