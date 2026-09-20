using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingBrokeredMessageOutboxProcessor
{
    public class WhenSendingOutboxMessages : Testing.Core.Context
    {
        // BrokeredMessageOutboxProcessor is internal, so Castle cannot proxy ILogger<BrokeredMessageOutboxProcessor>
        // (DynamicProxyGenAssembly2 lacks access to the SUT type in the closed generic). A hand-written
        // recording logger captures levels without a dynamic proxy.
        private sealed class RecordingLogger : ILogger<BrokeredMessageOutboxProcessor>
        {
            public List<LogLevel> Levels { get; } = new List<LogLevel>();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => Levels.Add(logLevel);

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }
        }

        private readonly RecordingLogger _logger = new RecordingLogger();
        private readonly ReliabilityOptions _reliabilityOptions = new ReliabilityOptions();
        private readonly Mock<IServiceScopeFactory> _serviceScopeFactory = new Mock<IServiceScopeFactory>();
        private readonly Mock<IServiceScope> _serviceScope = new Mock<IServiceScope>();
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly Mock<IBrokeredMessageOutbox> _outbox = new Mock<IBrokeredMessageOutbox>();
        private readonly Mock<IOutboxProcessor> _processor = new Mock<IOutboxProcessor>();
        private readonly BrokeredMessageOutboxProcessor _sut;

        private static readonly DateTime BaseSentToOutboxAtUtc = new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc);

        // RunContinuationsAsynchronously so the waiting test body never resumes INSIDE the poller's own call stack;
        // a test that stopped the poller from there would await a task it was itself standing on.
        private readonly TaskCompletionSource _pollSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _polls;
        private int _signalOnPoll = int.MaxValue;

        public WhenSendingOutboxMessages()
        {
            // A long interval parks the loop at Task.Delay after the first drain so a single
            // pass is observable and deterministic; the test signals completion off a TCS, never sleeps.
            _reliabilityOptions.OutboxProcessingIntervalInMilliseconds = 60000;

            // BrokeredMessageOutboxProcessor resolves IBrokeredMessageOutbox and casts to IPollableOutboxStore
            // at the consumption site. .As<IPollableOutboxStore>() must be called before .Object is accessed
            // (i.e. before any Setup that passes _outbox.Object to another mock) so Moq can build the
            // multi-interface proxy correctly.
            _outbox.As<IPollableOutboxStore>();

            _serviceScopeFactory.Setup(f => f.CreateScope()).Returns(_serviceScope.Object);
            _serviceScope.SetupGet(s => s.ServiceProvider).Returns(_serviceProvider.Object);
            // GetRequiredService<T>() resolves through GetService(Type); both must be set or it throws.
            _serviceProvider.Setup(p => p.GetService(typeof(IBrokeredMessageOutbox))).Returns(_outbox.Object);
            _serviceProvider.Setup(p => p.GetService(typeof(IOutboxProcessor))).Returns(_processor.Object);

            _sut = new BrokeredMessageOutboxProcessor(_logger, _reliabilityOptions, _serviceScopeFactory.Object);
        }

        private static OutboxMessage CreateOutboxMessage(int id, DateTime sentToOutboxAtUtc)
            => new OutboxMessage
            {
                Id = id,
                MessageId = $"message-{id}",
                Destination = "destination",
                SentToOutboxAtUtc = sentToOutboxAtUtc,
            };

        // InMemoryBrokeredMessageOutbox assigns no Id, so every row it hands back carries Id 0 and MessageId is all
        // that tells two of its rows apart.
        private static OutboxMessage CreateUnassignedIdOutboxMessage(int messageNumber, DateTime sentToOutboxAtUtc)
            => new OutboxMessage
            {
                Id = 0,
                MessageId = $"message-{messageNumber}",
                Destination = "destination",
                SentToOutboxAtUtc = sentToOutboxAtUtc,
            };

        private void SetupOutboxReturns(IEnumerable<OutboxMessage> messages)
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(messages);

        // Drives a single drain pass to completion: starts the hosted service, waits for the
        // supplied signal (fired from the last dependency invoked on the path under test), then stops.
        private Task RunSingleDrainAsync(Task signal)
            => RunSingleDrainAsync(_sut, signal);

        private static async Task RunSingleDrainAsync(BrokeredMessageOutboxProcessor sut, Task signal)
        {
            await sut.StartAsync(CancellationToken.None);
            await signal.WaitAsync(TimeSpan.FromSeconds(5));
            // Bounded so a drain loop that ignores its stopping token fails the test instead of hanging the suite.
            await sut.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Counts the poll and releases _pollSignal once the poller reaches the poll a test is waiting on, so a
        // test observes the drain off a signal rather than by sleeping for the processing interval.
        private int RecordPoll()
        {
            var poll = Interlocked.Increment(ref _polls);
            if (poll >= _signalOnPoll)
            {
                _pollSignal.TrySetResult();
            }
            return poll;
        }

        // Answers each poll from the script in order; the final script entry answers every poll after it.
        private void SetupScriptedPolls(params OutboxMessage[][] script)
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => script[Math.Min(RecordPoll(), script.Length) - 1]);

        // Answers each poll from the script CYCLICALLY, so no entry is ever the one that answers every poll after
        // it. A store that keeps rotating the same rows can therefore never end a drain by handing back an answer
        // positionally identical to the poll immediately before it.
        private void SetupCyclingPolls(params OutboxMessage[][] script)
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => script[(RecordPoll() - 1) % script.Length]);

        // Answers every poll with a FULL Outbox Poll Batch of rows never seen before, so neither a short batch nor
        // the no-progress guard can end the drain and only cancellation or the drain identity ceiling can.
        private void SetupEndlessDistinctFullPolls(int batchSize)
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateFullBatch(batchSize, RecordPoll()));

        private static OutboxMessage[] CreateFullBatch(int batchSize, int poll)
            => Enumerable.Range(0, batchSize)
                         .Select(offset => CreateOutboxMessage((poll * batchSize) + offset, BaseSentToOutboxAtUtc))
                         .ToArray();

        private void SignalWhenProcessedCountReaches(int target, List<int> processedOrder)
            => _processor.Setup(p => p.Process(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
                         .Returns(Task.CompletedTask)
                         .Callback<OutboxMessage, CancellationToken>((m, _) => RecordProcessed(m, target, processedOrder));

        private void RecordProcessed(OutboxMessage message, int target, List<int> processedOrder)
        {
            processedOrder.Add(message.Id);
            if (processedOrder.Count >= target)
            {
                _pollSignal.TrySetResult();
            }
        }

        private void VerifyPollCount(int expected)
            => _outbox.As<IPollableOutboxStore>()
                      .Verify(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()), Times.Exactly(expected));

        [Fact]
        public void MustThrowArgumentNullExceptionWhenLoggerIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageOutboxProcessor(null, _reliabilityOptions, _serviceScopeFactory.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenReliabilityOptionsIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageOutboxProcessor(_logger, null, _serviceScopeFactory.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServiceScopeFactoryIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageOutboxProcessor(_logger, _reliabilityOptions, null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustDrainUnprocessedMessagesFromOutbox()
        {
            var drained = new TaskCompletionSource();
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Enumerable.Empty<OutboxMessage>())
                   .Callback(() => drained.TrySetResult());

            await RunSingleDrainAsync(drained.Task);

            _outbox.As<IPollableOutboxStore>().Verify(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task MustCreateScopePerDrainPass()
        {
            var drained = new TaskCompletionSource();
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Enumerable.Empty<OutboxMessage>())
                   .Callback(() => drained.TrySetResult());

            await RunSingleDrainAsync(drained.Task);

            _serviceScopeFactory.Verify(f => f.CreateScope(), Times.AtLeastOnce);
        }

        [Fact]
        public async Task MustDelegateEachUnprocessedMessageToOutboxProcessor()
        {
            var processed = 0;
            var bothProcessed = new TaskCompletionSource();
            SetupOutboxReturns(new[]
            {
                CreateOutboxMessage(1, new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc)),
                CreateOutboxMessage(2, new DateTime(2026, 6, 7, 0, 1, 0, DateTimeKind.Utc)),
            });
            _processor.Setup(p => p.Process(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask)
                      .Callback(() =>
                      {
                          if (Interlocked.Increment(ref processed) == 2)
                          {
                              bothProcessed.TrySetResult();
                          }
                      });

            await RunSingleDrainAsync(bothProcessed.Task);

            _processor.Verify(p => p.Process(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Fact]
        public async Task MustProcessMessagesInAscendingSentToOutboxOrder()
        {
            var earlier = CreateOutboxMessage(1, new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc));
            var later = CreateOutboxMessage(2, new DateTime(2026, 6, 7, 0, 5, 0, DateTimeKind.Utc));
            // Supplied out of chronological order so OrderBy(m => m.SentToOutboxAtUtc) is exercised.
            SetupOutboxReturns(new[] { later, earlier });

            var processedOrder = new List<int>();
            var bothProcessed = new TaskCompletionSource();
            _processor.Setup(p => p.Process(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask)
                      .Callback<OutboxMessage, CancellationToken>((m, _) =>
                      {
                          processedOrder.Add(m.Id);
                          if (processedOrder.Count == 2)
                          {
                              bothProcessed.TrySetResult();
                          }
                      });

            await RunSingleDrainAsync(bothProcessed.Task);

            processedOrder.Should().Equal(earlier.Id, later.Id);
        }

        [Fact]
        public async Task MustNotProcessAnyMessageWhenOutboxIsEmpty()
        {
            var drained = new TaskCompletionSource();
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Enumerable.Empty<OutboxMessage>())
                   .Callback(() => drained.TrySetResult());

            await RunSingleDrainAsync(drained.Task);

            _processor.Verify(p => p.Process(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MustLogErrorAndNotTearDownPollerWhenDrainingThrows()
        {
            var attempted = new TaskCompletionSource();
            // The drain failure leaves messages pending; the loop's catch swallows it so the
            // poller survives and retries on the next interval rather than crashing.
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                   .Callback(() => attempted.TrySetResult())
                   .ThrowsAsync(new InvalidOperationException("boom"));

            await FluentActions.Invoking(() => RunSingleDrainAsync(attempted.Task))
                .Should().NotThrowAsync();

            VerifyErrorLogged();
        }

        [Fact]
        public async Task MustLogErrorAndNotTearDownPollerWhenProcessingThrows()
        {
            SetupOutboxReturns(new[] { CreateOutboxMessage(1, new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc)) });

            var attempted = new TaskCompletionSource();
            _processor.Setup(p => p.Process(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
                      .Callback(() => attempted.TrySetResult())
                      .ThrowsAsync(new InvalidOperationException("boom"));

            await FluentActions.Invoking(() => RunSingleDrainAsync(attempted.Task))
                .Should().NotThrowAsync();

            VerifyErrorLogged();
        }

        [Fact]
        public async Task MustRepollImmediatelyAfterAFullOutboxPollBatchAndStopAfterAShortOne()
        {
            // A poll bounded by OutboxPollBatchSize would otherwise trickle the backlog out one batch per
            // processing interval, capping throughput at the batch size every interval.
            _reliabilityOptions.OutboxPollBatchSize = 2;
            SetupScriptedPolls(
                new[] { CreateOutboxMessage(1, BaseSentToOutboxAtUtc), CreateOutboxMessage(2, BaseSentToOutboxAtUtc.AddMinutes(1)) },
                new[] { CreateOutboxMessage(3, BaseSentToOutboxAtUtc.AddMinutes(2)) });
            var processedOrder = new List<int>();
            SignalWhenProcessedCountReaches(3, processedOrder);

            await RunSingleDrainAsync(_pollSignal.Task);

            VerifyPollCount(2);
            processedOrder.Should().Equal(1, 2, 3);
        }

        [Fact]
        public async Task MustStopRepollingWhenAFullOutboxPollBatchRepeatsUnchanged()
        {
            // OutboxProcessor.Process swallows every dispatch failure, so a poison batch of exactly
            // OutboxPollBatchSize rows is re-fetched forever. Without this guard the drain never reaches its
            // Task.Delay and spins on the store with no wait at all. This is the simplest shape the repeat takes,
            // the batch coming back identical; MustStopRepollingWhenAFullOutboxPollBatchRepeatsInADifferentOrder
            // and MustStopRepollingWhenOverlappingOutboxPollBatchesAddNoUnseenMessage take the other two.
            _reliabilityOptions.OutboxPollBatchSize = 2;
            _signalOnPoll = 2;
            SetupScriptedPolls(new[] { CreateOutboxMessage(1, BaseSentToOutboxAtUtc), CreateOutboxMessage(2, BaseSentToOutboxAtUtc.AddMinutes(1)) });

            await RunSingleDrainAsync(_pollSignal.Task);

            VerifyPollCount(2);
        }

        [Fact]
        public async Task MustStopRepollingWhenAFullOutboxPollBatchRepeatsInADifferentOrder()
        {
            // GetUnprocessedMessagesFromOutbox orders by SentToOutboxAtUtc alone, so rows sharing one instant may
            // be handed back in any order and a poison batch can arrive reversed on every poll. Those are the same
            // two rows, so the second poll adds no message the drain has not already seen and must end it.
            _reliabilityOptions.OutboxPollBatchSize = 2;
            _signalOnPoll = 2;
            var first = CreateOutboxMessage(1, BaseSentToOutboxAtUtc);
            var second = CreateOutboxMessage(2, BaseSentToOutboxAtUtc);
            SetupCyclingPolls(new[] { first, second }, new[] { second, first });

            await RunSingleDrainAsync(_pollSignal.Task);

            VerifyPollCount(2);
        }

        [Fact]
        public async Task MustStopRepollingWhenOverlappingOutboxPollBatchesAddNoUnseenMessage()
        {
            // One more tied row than the batch takes, so which of them the store's cap keeps can differ per poll
            // and consecutive batches are different SUBSETS rather than reorderings of one another. Only the third
            // poll is wholly made of rows the drain has already seen, so that is the one that ends it.
            _reliabilityOptions.OutboxPollBatchSize = 2;
            _signalOnPoll = 3;
            var first = CreateUnassignedIdOutboxMessage(1, BaseSentToOutboxAtUtc);
            var second = CreateUnassignedIdOutboxMessage(2, BaseSentToOutboxAtUtc);
            var third = CreateUnassignedIdOutboxMessage(3, BaseSentToOutboxAtUtc);
            SetupCyclingPolls(new[] { first, second }, new[] { second, third });

            await RunSingleDrainAsync(_pollSignal.Task);

            VerifyPollCount(3);
        }

        [Fact]
        public async Task MustDispatchEveryRowAPollReturnsEvenWhenTheDrainHasAlreadySeenIt()
        {
            // The identity set terminates RE-POLLING; it never filters the batch before dispatch. The same overlapping
            // script as MustStopRepollingWhenOverlappingOutboxPollBatchesAddNoUnseenMessage, read on the dispatch side
            // rather than the poll side: three polls of two rows dispatch six times, and the row every poll carries is
            // dispatched on each of them even though the drain has held its identity since the first. The reddening
            // mutation is a dispatch loop that skips rows already in the set, which drops this drain to three
            // dispatches. The signal comes off the LAST dispatch rather than off the poll, so the third poll's
            // dispatches are complete before the count below is read.
            _reliabilityOptions.OutboxPollBatchSize = 2;
            var first = CreateOutboxMessage(1, BaseSentToOutboxAtUtc);
            var second = CreateOutboxMessage(2, BaseSentToOutboxAtUtc);
            var third = CreateOutboxMessage(3, BaseSentToOutboxAtUtc);
            SetupCyclingPolls(new[] { first, second }, new[] { second, third });
            var processedOrder = new List<int>();
            SignalWhenProcessedCountReaches(6, processedOrder);

            await RunSingleDrainAsync(_pollSignal.Task);

            VerifyPollCount(3);
            processedOrder.Should().Equal(1, 2, 2, 3, 1, 2);
            _processor.Verify(p => p.Process(It.Is<OutboxMessage>(m => m.Id == second.Id), It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        [Fact]
        public async Task MustEndTheDrainOnceTheDrainIdentityCeilingIsReached()
        {
            // Every poll answers full with rows never seen before, so nothing but the ceiling can end this drain.
            // Half the ceiling per batch reaches it on the second poll, rather than polling the default batch size
            // a hundred times. The signal comes off the LAST message of that poll rather than off the poll itself,
            // so a drain that kept going would reach a third poll before this test could stop it.
            _reliabilityOptions.OutboxPollBatchSize = BrokeredMessageOutboxProcessor.MaxDrainIdentities / 2;
            SetupEndlessDistinctFullPolls(_reliabilityOptions.OutboxPollBatchSize);
            SignalWhenProcessedCountReaches(BrokeredMessageOutboxProcessor.MaxDrainIdentities, new List<int>());

            await RunSingleDrainAsync(_pollSignal.Task);

            VerifyPollCount(2);
        }

        [Fact]
        public async Task MustEndTheDrainOnTheFirstPollThatCrossesTheDrainIdentityCeiling()
        {
            // MustEndTheDrainOnceTheDrainIdentityCeilingIsReached takes half the ceiling per batch and so lands on
            // it exactly. A batch that does NOT divide the ceiling evenly pins the arithmetic that one cannot see:
            // the ceiling is read AFTER the poll's identities are tallied, so the drain re-polls while the count is
            // still below 10,000 and the poll that crosses it adds a WHOLE batch on top. At 3,000 per poll the
            // drain therefore ends on the FOURTH poll having retained 12,000 identities, not on the ceiling itself.
            // The reddening mutation is a loop that stops SHORT of crossing the ceiling - reading it as
            // seenIdentities.Count + OutboxPollBatchSize <= MaxDrainIdentities - which ends this drain on the third
            // poll at 9,000 so the signal below never fires, and which leaves the exact-landing fact green. The
            // signal comes off the LAST message of the fourth poll rather than off the poll itself, so a drain that
            // kept going would reach a fifth poll before this test could stop it.
            _reliabilityOptions.OutboxPollBatchSize = 3000;
            SetupEndlessDistinctFullPolls(_reliabilityOptions.OutboxPollBatchSize);
            SignalWhenProcessedCountReaches(12000, new List<int>());

            await RunSingleDrainAsync(_pollSignal.Task);

            VerifyPollCount(4);
        }

        [Fact]
        public async Task MustNotRepollWhenTheFirstOutboxPollBatchIsEmpty()
        {
            _reliabilityOptions.OutboxPollBatchSize = 2;
            _signalOnPoll = 1;
            SetupScriptedPolls(Array.Empty<OutboxMessage>());

            await RunSingleDrainAsync(_pollSignal.Task);

            VerifyPollCount(1);
        }

        [Fact]
        public async Task MustStopDrainingWhenCancellationIsRequestedMidDrain()
        {
            // Every poll answers with a full batch of new rows, so nothing the store does ends this drain and the
            // identity ceiling is thousands of polls away at a batch of two: only cancellation can end it here.
            // RunSingleDrainAsync bounds StopAsync, so a drain that ignores its stopping token fails here.
            _reliabilityOptions.OutboxPollBatchSize = 2;
            _signalOnPoll = 3;
            SetupEndlessDistinctFullPolls(2);

            await FluentActions.Invoking(() => RunSingleDrainAsync(_pollSignal.Task))
                .Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustReleaseTheDrainScopeAsynchronously()
        {
            // SendOutboxMessagesAsync catches everything the poll raises, so a refused release surfaces as the
            // error log rather than as a throw out of the drain.
            var drained = new TaskCompletionSource();
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Enumerable.Empty<OutboxMessage>())
                   .Callback(() => drained.TrySetResult());

            await RunSingleDrainAsync(CreateSutHoldingAnAsyncOnlyDisposable(), drained.Task);

            _logger.Levels.Should().NotContain(LogLevel.Error);
        }

        // The Pollable Outbox Store resolved inside the poll's scope pulls an AsyncOnlyDisposableScopedService from
        // that same scope, so the scope holds a member a synchronous release refuses.
        private BrokeredMessageOutboxProcessor CreateSutHoldingAnAsyncOnlyDisposable()
        {
            var services = new ServiceCollection();
            services.AddScoped<AsyncOnlyDisposableScopedService>();
            services.AddScoped(provider => ResolveOutboxAfterAnAsyncOnlyDisposable(provider));
            services.AddScoped(_ => _processor.Object);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            return new BrokeredMessageOutboxProcessor(_logger, _reliabilityOptions, scopeFactory);
        }

        private IBrokeredMessageOutbox ResolveOutboxAfterAnAsyncOnlyDisposable(IServiceProvider provider)
        {
            provider.GetRequiredService<AsyncOnlyDisposableScopedService>();
            return _outbox.Object;
        }

        private void VerifyErrorLogged()
            => _logger.Levels.Should().Contain(LogLevel.Error);
    }
}
