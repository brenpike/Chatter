using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The single-pass contract of the dispatch sequence, asserted from the side of whatever walks it.
    /// </summary>
    /// <remarks>
    /// The sequence a dispatch call hands downstream is a <c>yield return</c> iterator whose body RE-RUNS on every
    /// enumeration: per message it resolves the destination, converts the body, constructs an
    /// <see cref="OutboundBrokeredMessage"/> — generating a message id — injects W3C Trace Context, and increments
    /// the send span's batch count. A second pass therefore repeats every one of those side effects and reports 2N
    /// on a batch of N. <c>IRouteBrokeredMessages.Route</c> and <c>IMessagingInfrastructureDispatcher.Dispatch</c>
    /// now DECLARE that contract; this class is what makes a violation of it DETECTABLE rather than merely
    /// undocumented.
    ///
    /// Every batch here is an <see cref="EnumerationProbeSequence{TItem}"/> rather than a
    /// <see cref="SinglePassEventSequence"/>, and the difference is the whole point: the probe PERMITS a second pass
    /// and records it, so a component that walks twice produces a wrong COUNT that these tests read, instead of
    /// dying inside a fixture that refused the walk. A refusal proves the walk did not happen; a recording proves
    /// what the telemetry would have said if it had.
    ///
    /// A probe over the CALLER'S batch measures passes over the dispatch sequence exactly, because the dispatch
    /// iterator pulls one caller message per yield: a downstream second pass re-runs that iterator and so requests a
    /// second enumerator from the caller's batch.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenDownstreamEnumeratesTheDispatchSequence : Testing.Core.Context
    {
        private const int BatchSize = 3;

        [Fact]
        public async Task MustCountEveryMessageExactlyOnceWhenTheBatchWouldPermitASecondPass()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var harness = new DiagnosticsSendHarness();
                var batch = BuildProbeBatch(BatchSize);

                await harness.PublishSequence(batch);

                // The defect this pins reported 2N: something downstream walked the dispatch sequence a second time,
                // which re-ran the iterator body and incremented the batch counter for every message all over again.
                batch.EnumeratorRequestCount.Should().Be(1);
                batch.YieldCountsPerPass.Should().Equal(new[] { BatchSize });

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(BatchSize);

                var sentMessages = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
                sentMessages.Value.Should().Be(BatchSize);

                harness.RoutedMessages.Should().HaveCount(BatchSize);
            }
        }

        [Fact]
        public async Task MustRequestOneEnumeratorWhenDiagnosticsAreNotOptedInto()
        {
            var harness = new DiagnosticsSendHarness();
            var batch = BuildProbeBatch(BatchSize);

            BrokerDiagnostics.IsEnabled.Should().BeFalse();

            await harness.PublishSequence(batch);

            batch.EnumeratorRequestCount.Should().Be(1);
            batch.PullTimeline.Should().Equal(BuildExpectedPullTimeline(BatchSize));
            harness.RoutedMessages.Should().HaveCount(BatchSize);
        }

        [Fact]
        public async Task MustRequestOneEnumeratorWhenDiagnosticsAreOptedInto()
        {
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var harness = new DiagnosticsSendHarness();
                var batch = BuildProbeBatch(BatchSize);

                BrokerDiagnostics.IsEnabled.Should().BeTrue();

                await harness.PublishSequence(batch);

                batch.EnumeratorRequestCount.Should().Be(1);
                batch.PullTimeline.Should().Equal(BuildExpectedPullTimeline(BatchSize));
                harness.RoutedMessages.Should().HaveCount(BatchSize);
            }
        }

        [Fact]
        public async Task MustEnumerateIdenticallyWhetherOrNotDiagnosticsAreOptedInto()
        {
            var unobservedHarness = new DiagnosticsSendHarness();
            var unobservedBatch = BuildProbeBatch(BatchSize);

            BrokerDiagnostics.IsEnabled.Should().BeFalse();

            await unobservedHarness.PublishSequence(unobservedBatch);

            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var observedHarness = new DiagnosticsSendHarness();
                var observedBatch = BuildProbeBatch(BatchSize);

                BrokerDiagnostics.IsEnabled.Should().BeTrue();

                await observedHarness.PublishSequence(observedBatch);

                // The acceptance criterion for this feature stated as a DIFFERENTIAL: instrumentation changes what is
                // reported about the walk, never the walk itself. Comparing the two recordings pins that directly,
                // where two independent absolute assertions would only pin it by coincidence of their expectations.
                observedBatch.EnumeratorRequestCount.Should().Be(unobservedBatch.EnumeratorRequestCount);
                observedBatch.YieldCountsPerPass.Should().Equal(unobservedBatch.YieldCountsPerPass);
                observedBatch.PullTimeline.Should().Equal(unobservedBatch.PullTimeline);
            }
        }

        [Fact]
        public async Task MustWalkTheDispatchSequenceOnceThroughTheBrokeredMessageRouter()
        {
            var routedMessages = new List<OutboundBrokeredMessage>();
            var infrastructureDispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            infrastructureDispatcher
                .Setup(dispatcher => dispatcher.Dispatch(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>()))
                .Callback<IEnumerable<OutboundBrokeredMessage>, TransactionContext>((outboundMessages, transactionContext) => routedMessages.AddRange(outboundMessages))
                .Returns(Task.CompletedTask);

            var infrastructureProvider = new Mock<IMessagingInfrastructureProvider>();
            infrastructureProvider.Setup(provider => provider.GetDispatcher(It.IsAny<string>())).Returns(infrastructureDispatcher.Object);

            var router = new BrokeredMessageRouter(infrastructureProvider.Object);
            var dispatchSequence = BuildOutboundProbeSequence(BatchSize);

            await router.Route(dispatchSequence, transactionContext: null, DiagnosticsSendHarness.MessagingSystem);

            // The Messaging Infrastructure stand-in walks the sequence exactly once, as the contract requires of it,
            // so any pass the Router added of its own would show up here as a second enumerator request.
            dispatchSequence.EnumeratorRequestCount.Should().Be(1);
            dispatchSequence.YieldCountsPerPass.Should().Equal(new[] { BatchSize });
            routedMessages.Should().HaveCount(BatchSize);
        }

        [Fact]
        public async Task MustWalkTheDispatchSequenceOnceThroughTheOutboxBrokeredMessageRouter()
        {
            var outbox = CreateInMemoryOutbox();
            var router = new OutboxBrokeredMessageRouter(outbox);
            var dispatchSequence = BuildOutboundProbeSequence(BatchSize);

            await router.Route(dispatchSequence, transactionContext: null, DiagnosticsSendHarness.MessagingSystem);

            // Composed over the real Outbox rather than a stand-in, because the Outbox is what actually walks the
            // sequence on this route: the pin is that the whole outbox-routed path costs exactly one pass.
            dispatchSequence.EnumeratorRequestCount.Should().Be(1);
            dispatchSequence.YieldCountsPerPass.Should().Equal(new[] { BatchSize });

            var unprocessed = await outbox.GetUnprocessedMessagesFromOutbox();
            unprocessed.Should().HaveCount(BatchSize);
        }

        [Fact]
        public async Task MustWalkTheDispatchSequenceOnceThroughTheInMemoryOutbox()
        {
            var outbox = CreateInMemoryOutbox();
            var dispatchSequence = BuildOutboundProbeSequence(BatchSize);

            await outbox.SendToOutbox(dispatchSequence, transactionContext: null);

            // A second pass would hand the Outbox the same message ids again, which its add refuses outright — so
            // the recorded request count is the earlier and more precise signal of the same violation.
            dispatchSequence.EnumeratorRequestCount.Should().Be(1);
            dispatchSequence.YieldCountsPerPass.Should().Equal(new[] { BatchSize });

            var unprocessed = await outbox.GetUnprocessedMessagesFromOutbox();
            unprocessed.Should().HaveCount(BatchSize);
        }

        private static EnumerationProbeSequence<TracedEvent> BuildProbeBatch(int messageCount)
        {
            var messages = new List<TracedEvent>(messageCount);

            for (var index = 0; index < messageCount; index++)
            {
                messages.Add(new TracedEvent { Value = "event-" + index });
            }

            return new EnumerationProbeSequence<TracedEvent>(messages, new List<string>());
        }

        private static EnumerationProbeSequence<OutboundBrokeredMessage> BuildOutboundProbeSequence(int messageCount)
        {
            var bodyConverter = new JsonBodyConverter();
            var outboundMessages = new List<OutboundBrokeredMessage>(messageCount);

            for (var index = 0; index < messageCount; index++)
            {
                outboundMessages.Add(new OutboundBrokeredMessage(
                    "message-" + index,
                    new TracedEvent { Value = "event-" + index },
                    new Dictionary<string, object>(),
                    DiagnosticsSendHarness.DestinationPath,
                    bodyConverter));
            }

            return new EnumerationProbeSequence<OutboundBrokeredMessage>(outboundMessages, new List<string>());
        }

        private static InMemoryBrokeredMessageOutbox CreateInMemoryOutbox()
            => new InMemoryBrokeredMessageOutbox(NullLogger<InMemoryBrokeredMessageOutbox>.Instance, new ReliabilityOptions());

        /// <summary>
        /// The entries a fully-walked probe must record: exactly one enumerator request, then one entry per item.
        /// </summary>
        private static string[] BuildExpectedPullTimeline(int yieldedCount)
        {
            var timeline = new List<string> { EnumerationProbeSequence<TracedEvent>.EnumeratorRequestedEntry };

            for (var index = 0; index < yieldedCount; index++)
            {
                timeline.Add(EnumerationProbeSequence<TracedEvent>.YieldedEntryPrefix + index);
            }

            return timeline.ToArray();
        }
    }
}
