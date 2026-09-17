using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingServiceBusMessageSender
{
    // Pins the LIFETIME of the transaction scope the batch Dispatch overload runs under: the scope must
    // stay open until every send it started has completed, and only then be completed and disposed.
    // Transaction.Current cannot discriminate the fixed sender from the broken one here - the caller
    // reads the ambient transaction either way - so these tests drive the scope through the internal
    // IDispatchTransactionScopeFactory seam and assert scope disposal ORDERING against gated sends.
    public class WhenDispatchingUnderSuppressedTransaction : Testing.Core.Context
    {
        private readonly byte[] _body = new byte[] { 1, 2, 3 };
        private readonly JsonBodyConverter _converter = new JsonBodyConverter();

        private OutboundBrokeredMessage CreateBrokeredMessage(string messageId)
            => new OutboundBrokeredMessage(messageId, _body, new Dictionary<string, object>(), "destination", _converter);

        // Records when it was completed and disposed, and how many sends had finished at the instant of
        // disposal, so a scope that closes over in-flight sends is distinguishable from one that does not.
        private class RecordingDispatchTransactionScope : IDispatchTransactionScope
        {
            private readonly Func<int> _readCompletedSendCount;

            public RecordingDispatchTransactionScope(Func<int> readCompletedSendCount)
                => _readCompletedSendCount = readCompletedSendCount;

            public bool IsDisposed { get; private set; }
            public int CompleteCallCount { get; private set; }
            public bool WasCompletedBeforeDisposed { get; private set; }
            public int CompletedSendCountAtDisposal { get; private set; } = -1;

            public void Complete()
            {
                CompleteCallCount++;
                WasCompletedBeforeDisposed = !IsDisposed;
            }

            public void Dispose()
            {
                CompletedSendCountAtDisposal = _readCompletedSendCount();
                IsDisposed = true;
            }
        }

        private class RecordingDispatchTransactionScopeFactory : IDispatchTransactionScopeFactory
        {
            private readonly Func<int> _readCompletedSendCount;

            public RecordingDispatchTransactionScopeFactory(Func<int> readCompletedSendCount)
                => _readCompletedSendCount = readCompletedSendCount;

            public List<TransactionMode> RequestedTransactionModes { get; } = new List<TransactionMode>();
            public RecordingDispatchTransactionScope Scope { get; private set; }

            public IDispatchTransactionScope Create(TransactionMode transactionMode)
            {
                RequestedTransactionModes.Add(transactionMode);
                Scope = new RecordingDispatchTransactionScope(_readCompletedSendCount);
                return Scope;
            }
        }

        // Hands back senders whose SendMessageAsync stays in flight until the test opens the gate, so the
        // window between starting a send and its completion is under the test's control.
        private class GatedSenderFactory : IServiceBusMessageSenderFactory
        {
            private int _completedSendCount;

            public List<TaskCompletionSource<bool>> Gates { get; } = new List<TaskCompletionSource<bool>>();

            public int CompletedSendCount => Volatile.Read(ref _completedSendCount);

            public ServiceBusSender Create(string destinationEntityPath)
            {
                var sender = new Mock<ServiceBusSender>();
                sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
                      .Returns((ServiceBusMessage _, CancellationToken __) => StartGatedSend());
                return sender.Object;
            }

            public void OpenEveryGate()
            {
                foreach (var gate in Gates)
                {
                    gate.TrySetResult(true);
                }
            }

            private Task StartGatedSend()
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Gates.Add(gate);
                return AwaitGateAsync(gate);
            }

            private async Task AwaitGateAsync(TaskCompletionSource<bool> gate)
            {
                await gate.Task;
                Interlocked.Increment(ref _completedSendCount);
            }
        }

        private static async Task DrainAsync(GatedSenderFactory factory, Task dispatchTask)
        {
            factory.OpenEveryGate();
            await dispatchTask;
        }

        [Fact]
        public async Task MustNotDisposeScopeWhileSendsAreInFlight()
        {
            var senderFactory = new GatedSenderFactory();
            var scopeFactory = new RecordingDispatchTransactionScopeFactory(() => senderFactory.CompletedSendCount);
            var sut = new ServiceBusMessageSender(senderFactory, scopeFactory);

            var dispatchTask = sut.Dispatch(new[] { CreateBrokeredMessage("first"), CreateBrokeredMessage("second") },
                                            new TransactionContext("receiver", TransactionMode.ReceiveOnly));

            scopeFactory.Scope.IsDisposed.Should().BeFalse();

            await DrainAsync(senderFactory, dispatchTask);
        }

        [Fact]
        public async Task MustDisposeScopeOnlyAfterEverySendCompleted()
        {
            var senderFactory = new GatedSenderFactory();
            var scopeFactory = new RecordingDispatchTransactionScopeFactory(() => senderFactory.CompletedSendCount);
            var sut = new ServiceBusMessageSender(senderFactory, scopeFactory);

            var dispatchTask = sut.Dispatch(new[] { CreateBrokeredMessage("first"), CreateBrokeredMessage("second") },
                                            new TransactionContext("receiver", TransactionMode.ReceiveOnly));

            await DrainAsync(senderFactory, dispatchTask);

            scopeFactory.Scope.CompletedSendCountAtDisposal.Should().Be(2);
        }

        [Fact]
        public async Task MustCompleteScopeBeforeDisposingIt()
        {
            var senderFactory = new GatedSenderFactory();
            var scopeFactory = new RecordingDispatchTransactionScopeFactory(() => senderFactory.CompletedSendCount);
            var sut = new ServiceBusMessageSender(senderFactory, scopeFactory);

            var dispatchTask = sut.Dispatch(new[] { CreateBrokeredMessage("first") },
                                            new TransactionContext("receiver", TransactionMode.ReceiveOnly));

            await DrainAsync(senderFactory, dispatchTask);

            scopeFactory.Scope.CompleteCallCount.Should().Be(1);
            scopeFactory.Scope.WasCompletedBeforeDisposed.Should().BeTrue();
            scopeFactory.Scope.IsDisposed.Should().BeTrue();
        }

        [Fact]
        public async Task MustCreateScopeForTheDispatchTransactionMode()
        {
            var senderFactory = new GatedSenderFactory();
            var scopeFactory = new RecordingDispatchTransactionScopeFactory(() => senderFactory.CompletedSendCount);
            var sut = new ServiceBusMessageSender(senderFactory, scopeFactory);

            var dispatchTask = sut.Dispatch(CreateBrokeredMessage("first"),
                                            new TransactionContext("receiver", TransactionMode.ReceiveOnly));

            await DrainAsync(senderFactory, dispatchTask);

            scopeFactory.RequestedTransactionModes.Should().Equal(new[] { TransactionMode.ReceiveOnly });
        }

        [Fact]
        public void MustSuppressAmbientTransactionForReceiveOnlyDispatch()
        {
            var factory = new DispatchTransactionScopeFactory();

            using var ambient = new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled);
            using (var scope = factory.Create(TransactionMode.ReceiveOnly))
            {
                Transaction.Current.Should().BeNull();
                scope.Complete();
            }

            Transaction.Current.Should().NotBeNull();
        }

        [Fact]
        public void MustLeaveAmbientTransactionInPlaceForNonReceiveOnlyDispatch()
        {
            var factory = new DispatchTransactionScopeFactory();

            using var ambient = new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled);
            var ambientTransaction = Transaction.Current;

            using (var scope = factory.Create(TransactionMode.None))
            {
                Transaction.Current.Should().BeSameAs(ambientTransaction);
                scope.Complete();
            }

            Transaction.Current.Should().BeSameAs(ambientTransaction);
        }
    }
}
