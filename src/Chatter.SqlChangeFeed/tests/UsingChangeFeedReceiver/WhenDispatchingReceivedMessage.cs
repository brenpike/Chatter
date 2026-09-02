using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingChangeFeedReceiver
{
    /// <summary>
    /// Pins the per-change-item dispatch routing of
    /// <see cref="ChangeFeedReceiver{TRowChangeData}.DispatchReceivedMessageAsync"/> as-is: an
    /// <c>Inserted</c>/<c>Deleted</c> pairing on each <see cref="ChangeFeedItem{TRowChangeData}"/>
    /// selects the corresponding row event type dispatched via <see cref="IMessageDispatcher"/>.
    /// </summary>
    public class WhenDispatchingReceivedMessage : Testing.Core.Context
    {
        private readonly Mock<IMessageDispatcher> _dispatcher = new Mock<IMessageDispatcher>();
        private readonly Mock<IBrokeredMessageDispatcher> _brokeredDispatcher = new Mock<IBrokeredMessageDispatcher>();

        private ChangeFeedReceiver<FakeRowData> CreateReceiver()
        {
            var serviceProvider = new Mock<IServiceProvider>();
            serviceProvider.Setup(p => p.GetService(typeof(IMessageDispatcher))).Returns(_dispatcher.Object);
            serviceProvider.Setup(p => p.GetService(typeof(IBrokeredMessageDispatcher))).Returns(_brokeredDispatcher.Object);

            var scope = new Mock<IServiceScope>();
            scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            return new ChangeFeedReceiver<FakeRowData>(
                new Mock<IMessagingInfrastructureProvider>().Object,
                new MessageBrokerOptions(),
                NullLogger<BrokeredMessageReceiver<ProcessChangeFeedCommand<FakeRowData>>>.Instance,
                scopeFactory.Object,
                new Mock<IMaxReceivesExceededAction>().Object,
                new Mock<ICriticalFailureNotifier>().Object,
                new Mock<IRecoveryStrategy>().Object,
                new Mock<IReceivedMessageDispatcher>().Object);
        }

        private static MessageBrokerContext CreateContext()
            => new MessageBrokerContext("message-id", Array.Empty<byte>(),
                new Dictionary<string, object>(), "receiver-path", CancellationToken.None,
                new Mock<IBrokeredMessageBodyConverter>().Object);

        private static ProcessChangeFeedCommand<FakeRowData> Command(params ChangeFeedItem<FakeRowData>[] items)
            => new ProcessChangeFeedCommand<FakeRowData> { Changes = items };

        [Fact]
        public async Task MustDispatchSingleRowUpdatedEventWhenItemHasBothInsertedAndDeleted()
        {
            var inserted = new FakeRowData { Id = 1, Name = "new" };
            var deleted = new FakeRowData { Id = 1, Name = "old" };
            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = inserted, Deleted = deleted });

            await CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), CancellationToken.None);

            _dispatcher.Verify(d => d.Dispatch(
                It.Is<RowUpdatedEvent<FakeRowData>>(e => e.NewValue == inserted && e.OldValue == deleted),
                It.IsAny<IMessageHandlerContext>()), Times.Once);
        }

        [Fact]
        public async Task MustDispatchSingleRowInsertedEventWhenItemHasInsertedOnly()
        {
            var inserted = new FakeRowData { Id = 2, Name = "ins" };
            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = inserted, Deleted = null });

            await CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), CancellationToken.None);

            _dispatcher.Verify(d => d.Dispatch(
                It.Is<RowInsertedEvent<FakeRowData>>(e => e.Inserted == inserted),
                It.IsAny<IMessageHandlerContext>()), Times.Once);
        }

        [Fact]
        public async Task MustDispatchSingleRowDeletedEventWhenItemHasDeletedOnly()
        {
            var deleted = new FakeRowData { Id = 3, Name = "del" };
            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = null, Deleted = deleted });

            await CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), CancellationToken.None);

            _dispatcher.Verify(d => d.Dispatch(
                It.Is<RowDeletedEvent<FakeRowData>>(e => e.Deleted == deleted),
                It.IsAny<IMessageHandlerContext>()), Times.Once);
        }

        [Fact]
        public async Task MustNotDispatchWhenItemHasNeitherInsertedNorDeleted()
        {
            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = null, Deleted = null });

            await CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), CancellationToken.None);

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Never);
        }

        [Fact]
        public async Task MustNotThrowWhenItemHasNeitherInsertedNorDeleted()
        {
            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = null, Deleted = null });

            await FluentActions.Invoking(() =>
                    CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), CancellationToken.None))
                .Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustNotDispatchWhenChangesAreEmpty()
        {
            var command = Command();

            await CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), CancellationToken.None);

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Never);
        }

        [Fact]
        public async Task MustDispatchEachEventTypeTheExpectedNumberOfTimesForMixedBatch()
        {
            var command = Command(
                new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData(), Deleted = new FakeRowData() },
                new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData() },
                new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData() },
                new ChangeFeedItem<FakeRowData> { Deleted = new FakeRowData() },
                new ChangeFeedItem<FakeRowData>());

            await CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), CancellationToken.None);

            _dispatcher.Verify(d => d.Dispatch(
                It.IsAny<RowUpdatedEvent<FakeRowData>>(), It.IsAny<IMessageHandlerContext>()), Times.Once);
            _dispatcher.Verify(d => d.Dispatch(
                It.IsAny<RowInsertedEvent<FakeRowData>>(), It.IsAny<IMessageHandlerContext>()), Times.Exactly(2));
            _dispatcher.Verify(d => d.Dispatch(
                It.IsAny<RowDeletedEvent<FakeRowData>>(), It.IsAny<IMessageHandlerContext>()), Times.Once);
        }

        [Fact]
        public async Task MustThrowOperationCanceledExceptionWhenTokenIsAlreadyCancelled()
        {
            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData() });
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await FluentActions.Invoking(() =>
                    CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), cancelled.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task MustNotDispatchWhenTokenIsAlreadyCancelled()
        {
            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData() });
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            try
            {
                await CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                // INVARIANT: cancellation is checked before any dispatch; the throw is expected here and the
                // assertion below pins that no dispatch occurred prior to it.
            }

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Never);
        }

        [Fact]
        public async Task MustIncludeBrokeredMessageDispatcherAsExternalDispatcherInContextContainer()
        {
            var context = CreateContext();
            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData() });

            await CreateReceiver().DispatchReceivedMessageAsync(command, context, CancellationToken.None);

            context.Container.TryGet<IExternalDispatcher>(out var external).Should().BeTrue();
            external.Should().BeSameAs(_brokeredDispatcher.Object);
        }

        // INVARIANT: a task returned from a non-async delegate that is completed from a background thread only after
        // a genuine delay, never touching the ambient SynchronizationContext itself. The delay guarantees the
        // returned task is still incomplete at the moment the receiver's `await` statement observes it, forcing a
        // real asynchronous suspension rather than a synchronous completion the awaiter could skip scheduling for
        // (a task already complete at await-time never registers a continuation, so it would prove nothing). This
        // isolates the assertion to the receiver's OWN await behavior: any Post recorded below can only come from
        // the receiver's per-change-item `await dispatcher.Dispatch(...)` capturing the ambient context, not from
        // anything internal to the mock.
        private static Task DeferredDispatchTask()
        {
            var completionSource = new TaskCompletionSource<object>();
            Task.Run(() =>
            {
                Thread.Sleep(50);
                completionSource.SetResult(null);
            });
            return completionSource.Task;
        }

        private sealed class RecordingSynchronizationContext : SynchronizationContext
        {
            private int _postCount;

            public int PostCount => Volatile.Read(ref _postCount);

            public override void Post(SendOrPostCallback d, object state)
            {
                Interlocked.Increment(ref _postCount);
                base.Post(d, state);
            }
        }

        // INVARIANT: runs a single command through the receiver under a recording SynchronizationContext and asserts
        // no continuation was posted back to it. Each of the three call sites below drives exactly ONE
        // per-change-item dispatch call (one item, one branch), so the assertion pins THAT SPECIFIC
        // `await dispatcher.Dispatch(...)` call site independently of the other two. A single mixed-item batch
        // would not do this reliably: once the first await's continuation resumes via Post, whether the ambient
        // SynchronizationContext is still observably current for a LATER await in the same loop iteration is an
        // artifact of the runtime's continuation-wrapping, not something this test should lean on. One item per
        // test isolates each call site on its own merits.
        private void AssertNoContinuationPosted(ProcessChangeFeedCommand<FakeRowData> command)
        {
            var recordingContext = new RecordingSynchronizationContext();
            var originalContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(recordingContext);
            try
            {
                // Blocking GetResult(), not await, so the test method itself never registers its own continuation
                // against recordingContext; any Post recorded below is solely attributable to the receiver's
                // internal per-change-item dispatch await.
                CreateReceiver().DispatchReceivedMessageAsync(command, CreateContext(), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(originalContext);
            }

            recordingContext.PostCount.Should().Be(0,
                "the per-change-item dispatch await must use ConfigureAwait(false) so no continuation is posted back to a caller's SynchronizationContext");
        }

        [Fact]
        public void MustNotPostContinuationToCapturedSynchronizationContextWhenDispatchingRowUpdatedEvent()
        {
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<RowUpdatedEvent<FakeRowData>>(), It.IsAny<IMessageHandlerContext>()))
                .Returns(DeferredDispatchTask);

            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData(), Deleted = new FakeRowData() });

            AssertNoContinuationPosted(command);
        }

        [Fact]
        public void MustNotPostContinuationToCapturedSynchronizationContextWhenDispatchingRowInsertedEvent()
        {
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<RowInsertedEvent<FakeRowData>>(), It.IsAny<IMessageHandlerContext>()))
                .Returns(DeferredDispatchTask);

            var command = Command(new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData() });

            AssertNoContinuationPosted(command);
        }

        [Fact]
        public void MustNotPostContinuationToCapturedSynchronizationContextWhenDispatchingRowDeletedEvent()
        {
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<RowDeletedEvent<FakeRowData>>(), It.IsAny<IMessageHandlerContext>()))
                .Returns(DeferredDispatchTask);

            var command = Command(new ChangeFeedItem<FakeRowData> { Deleted = new FakeRowData() });

            AssertNoContinuationPosted(command);
        }
    }
}
