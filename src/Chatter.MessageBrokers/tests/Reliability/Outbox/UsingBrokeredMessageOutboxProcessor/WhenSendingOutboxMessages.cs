using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
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

        private void SetupOutboxReturns(IEnumerable<OutboxMessage> messages)
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.GetUnprocessedMessagesFromOutbox(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(messages);

        // Drives a single drain pass to completion: starts the hosted service, waits for the
        // supplied signal (fired from the last dependency invoked on the path under test), then stops.
        private async Task RunSingleDrainAsync(Task signal)
        {
            await _sut.StartAsync(CancellationToken.None);
            await signal.WaitAsync(TimeSpan.FromSeconds(5));
            await _sut.StopAsync(CancellationToken.None);
        }

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

        private void VerifyErrorLogged()
            => _logger.Levels.Should().Contain(LogLevel.Error);
    }
}
