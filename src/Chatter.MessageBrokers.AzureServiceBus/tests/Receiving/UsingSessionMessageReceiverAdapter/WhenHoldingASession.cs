using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingSessionMessageReceiverAdapter
{
    // Pins what AzureSdkSessionMessageReceiverAdapter does WHILE it holds a session — the paths its sibling
    // WhenSettlingWithoutHeldSession cannot reach, because ServiceBusSessionReceiver is sealed and only
    // obtainable from a live namespace. The session is handed to the adapter through its injected acceptance
    // delegate as an IServiceBusHeldSession, so acquiring, serving, settling and releasing a session are all
    // driven here with no live Azure Service Bus namespace; the clock never moves and every renewal wait PARKS,
    // so no assertion sleeps or depends on wall-clock timing.
    public class WhenHoldingASession : Testing.Core.Context
    {
        private const string _receiverPath = "session-queue";
        private static readonly DateTimeOffset _startOfTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan _sessionIdleTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

        private readonly ParkedDelayGate _delays = new ParkedDelayGate();
        private readonly InMemoryHeldSession _session = new InMemoryHeldSession("session-a");
        private Exception _acceptFailure;
        private int _acceptCount;

        [Fact]
        public void MustAnswerTheConcreteSdkSessionReceiverFromBothSessionPorts()
        {
            // The transaction Container keys by the STATIC type of what it is handed and the PUBLIC session-state
            // extension resolves it by that same concrete type, so widening either answer to the held-session port
            // would leave every GetSessionStateAsync/SetSessionStateAsync call resolving nothing and throwing.
            var sessionReceiverFor = typeof(IServiceBusSessionMessageReceiver).GetMethod(nameof(IServiceBusSessionMessageReceiver.SessionReceiverFor));
            var heldSessionReceiver = typeof(IServiceBusSessionChildReceiver).GetProperty(nameof(IServiceBusSessionChildReceiver.HeldSessionReceiver));

            sessionReceiverFor.ReturnType.Should().Be(typeof(ServiceBusSessionReceiver));
            heldSessionReceiver.PropertyType.Should().Be(typeof(ServiceBusSessionReceiver));
        }

        [Fact]
        public async Task MustAnswerNoSdkSessionReceiverForAHeldSessionThatHasNone()
        {
            var sut = CreateSut();

            await WithHeldSessionAsync(sut, () =>
            {
                sut.HeldSessionReceiver.Should().BeNull("the held session in this test carries no SDK session receiver");
                sut.SessionReceiverFor(AnyMessage()).Should().BeNull();
            });
        }

        [Fact]
        public async Task MustExposeTheHeldSessionsFacts()
        {
            _session.SessionLockedUntil = _startOfTime.AddMinutes(3);
            var sut = CreateSut();

            await WithHeldSessionAsync(sut, () =>
            {
                sut.HeldSessionId.Should().Be("session-a");
                sut.HeldSessionLockedUntil.Should().Be(_startOfTime.AddMinutes(3));
            });
        }

        [Fact]
        public async Task MustServeTheHeldSessionsMessage()
        {
            var delivered = AnyMessage();
            var sut = CreateSut();

            var receiving = sut.ReceiveAsync(CancellationToken.None);
            await _session.WaitForReceiveCountAsync(1, _waitTimeout);
            _session.YieldMessage(delivered);

            (await receiving).Should().BeSameAs(delivered);
            _session.LastReceiveMaxWaitTime.Should().Be(_sessionIdleTimeout, "a held session that yields nothing within the idle timeout is what rolls the session");
        }

        [Fact]
        public async Task MustAcceptNoFurtherSessionWhileOneIsHeld()
        {
            var sut = CreateSut();

            await ReceiveOneMessageAsync(sut);
            await ReceiveOneMessageAsync(sut);

            _acceptCount.Should().Be(1, "the adapter holds ONE session until it is released, so a second receive serves the session already held");
        }

        [Fact]
        public async Task MustCompleteOnTheHeldSession()
        {
            var settled = AnyMessage();
            var sut = CreateSut();
            await ReceiveOneMessageAsync(sut, settled);

            var outcome = await sut.CompleteAsync(settled);

            outcome.Should().Be(ServiceBusSettlementOutcome.Settled);
            _session.CompletedMessages.Should().ContainSingle().Which.Should().BeSameAs(settled);
        }

        [Fact]
        public async Task MustAbandonOnTheHeldSession()
        {
            var settled = AnyMessage();
            var propertiesToModify = new Dictionary<string, object> { ["reason"] = "retry" };
            var sut = CreateSut();
            await ReceiveOneMessageAsync(sut, settled);

            var outcome = await sut.AbandonAsync(settled, propertiesToModify);

            outcome.Should().Be(ServiceBusSettlementOutcome.Settled);
            _session.AbandonedMessages.Should().ContainSingle().Which.Should().BeSameAs(settled);
            _session.AbandonPropertiesToModify.Should().ContainSingle().Which.Should().BeSameAs(propertiesToModify);
        }

        [Fact]
        public async Task MustDeadLetterOnTheHeldSession()
        {
            var settled = AnyMessage();
            var sut = CreateSut();
            await ReceiveOneMessageAsync(sut, settled);

            var outcome = await sut.DeadLetterAsync(settled, "reason", "description");

            outcome.Should().Be(ServiceBusSettlementOutcome.Settled);
            _session.DeadLetteredMessages.Should().ContainSingle()
                    .Which.Should().Be((settled, "reason", "description"));
        }

        [Fact]
        public async Task MustReleaseTheHeldSessionWhenItYieldsNoMessage()
        {
            var sut = CreateSut();

            var receiving = sut.ReceiveAsync(CancellationToken.None);
            await _session.WaitForReceiveCountAsync(1, _waitTimeout);
            _session.YieldNoMessage();

            (await receiving).Should().BeNull("a drained or idle session rolls: the pump re-polls and the adapter accepts the next session");
            _session.CloseCount.Should().Be(1);
            sut.HeldSessionId.Should().BeNull();
        }

        [Fact]
        public async Task MustReleaseTheHeldSessionWhenItsLockIsLost()
        {
            var sut = CreateSut();

            var receiving = sut.ReceiveAsync(CancellationToken.None);
            await _session.WaitForReceiveCountAsync(1, _waitTimeout);
            _session.FailReceive(new ServiceBusException("session lock lost", ServiceBusFailureReason.SessionLockLost));

            // An expected operational event, NOT a receiver-stopping fault: the session is released and the pump
            // re-polls rather than the receive raising.
            (await receiving).Should().BeNull();
            _session.CloseCount.Should().Be(1);
            sut.HeldSessionId.Should().BeNull();
        }

        [Theory]
        [InlineData(ServiceBusFailureReason.ServiceTimeout)]
        [InlineData(ServiceBusFailureReason.SessionCannotBeLocked)]
        public async Task MustRePollWhenNoSessionCanBeAccepted(ServiceBusFailureReason reason)
        {
            _acceptFailure = new ServiceBusException("no session available", reason);
            var sut = CreateSut();

            var received = await sut.ReceiveAsync(CancellationToken.None);

            received.Should().BeNull("no session available right now is non-fatal — the pump re-polls and the adapter tries the next session");
            sut.HeldSessionId.Should().BeNull();
        }

        [Fact]
        public async Task MustCloseTheHeldSessionOnClose()
        {
            var sut = CreateSut();
            await ReceiveOneMessageAsync(sut);

            await sut.CloseAsync();

            _session.CloseCount.Should().Be(1);
            sut.IsClosedOrClosing.Should().BeTrue();
        }

        [Fact]
        public async Task MustRenewTheHeldSessionsLockOnTheInjectedDelay()
        {
            var sut = CreateSut(maxSessionLockRenewalDuration: TimeSpan.FromHours(1));
            await ReceiveOneMessageAsync(sut);

            await _delays.WaitForParkedDelayAsync(_waitTimeout);
            _delays.RenewalTokens.Should().ContainSingle("the held session's renewal waits on the adapter's injected delay, not on the wall clock");

            // The release path cancels the renewal and AWAITS it before closing the session, so a close returning
            // at all is what proves the parked renewal ended first.
            await sut.CloseAsync();

            _delays.RenewalTokens[0].IsCancellationRequested.Should().BeTrue();
            _session.CloseCount.Should().Be(1);
        }

        // Holds a session, runs the assertions while it is held, then lets the parked receive finish.
        private async Task WithHeldSessionAsync(AzureSdkSessionMessageReceiverAdapter sut, Action assert)
        {
            var receiving = sut.ReceiveAsync(CancellationToken.None);
            await _session.WaitForReceiveCountAsync(1, _waitTimeout);

            assert();

            _session.YieldMessage(AnyMessage());
            await receiving;
        }

        private async Task ReceiveOneMessageAsync(AzureSdkSessionMessageReceiverAdapter sut, ServiceBusReceivedMessage message = null)
        {
            var alreadyReceived = _session.ReceiveCount;
            var receiving = sut.ReceiveAsync(CancellationToken.None);
            await _session.WaitForReceiveCountAsync(alreadyReceived + 1, _waitTimeout);
            _session.YieldMessage(message ?? AnyMessage());
            await receiving;
        }

        private Task<IServiceBusHeldSession> AcceptNextSessionAsync(CancellationToken cancellationToken)
        {
            _acceptCount++;

            return _acceptFailure == null
                ? Task.FromResult<IServiceBusHeldSession>(_session)
                : Task.FromException<IServiceBusHeldSession>(_acceptFailure);
        }

        private AzureSdkSessionMessageReceiverAdapter CreateSut(ServiceBusReceiveMode receiveMode = ServiceBusReceiveMode.PeekLock,
                                                                TimeSpan? maxSessionLockRenewalDuration = null)
            => new AzureSdkSessionMessageReceiverAdapter(ServiceBusSessionEntityPath.Create(string.Empty, _receiverPath),
                                                         receiveMode,
                                                         _sessionIdleTimeout,
                                                         maxSessionLockRenewalDuration ?? TimeSpan.Zero,
                                                         Mock.Of<ILogger>(),
                                                         AcceptNextSessionAsync,
                                                         new FixedTimeProvider(_startOfTime),
                                                         _delays.DelayAsync);

        private static ServiceBusReceivedMessage AnyMessage() => ServiceBusMessageFactory.ReceivedMessage(sessionId: "session-a");

        /// <summary>
        /// A <see cref="TimeProvider"/> whose UTC now never moves, so no renewal reaches its ceiling and a started
        /// renewal stays observable for the duration of a test.
        /// </summary>
        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _utcNow;

            public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

            public override DateTimeOffset GetUtcNow() => _utcNow;
        }

        /// <summary>
        /// Stands in for <see cref="Task.Delay(TimeSpan, CancellationToken)"/>: it records the renewal token that
        /// asked to wait, then PARKS until that token is cancelled. Parking is what keeps the started renewal alive
        /// and observable with no wall-clock sleep.
        /// </summary>
        private sealed class ParkedDelayGate
        {
            private readonly object _syncLock = new object();
            private readonly List<CancellationToken> _renewalTokens = new List<CancellationToken>();
            private TaskCompletionSource<bool> _parkedObservedSource
                = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<CancellationToken> RenewalTokens
            {
                get
                {
                    lock (_syncLock)
                    {
                        return _renewalTokens.ToArray();
                    }
                }
            }

            public Task DelayAsync(TimeSpan delay, CancellationToken renewalToken)
            {
                var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> observed;
                lock (_syncLock)
                {
                    _renewalTokens.Add(renewalToken);
                    observed = _parkedObservedSource;
                    _parkedObservedSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                renewalToken.Register(() => parked.TrySetCanceled(renewalToken));
                observed.TrySetResult(true);
                return parked.Task;
            }

            // Waits until a renewal has actually parked on this gate, without polling or sleeping. Throws rather
            // than hanging when none ever does.
            public async Task WaitForParkedDelayAsync(TimeSpan timeout)
            {
                Task observed;
                lock (_syncLock)
                {
                    if (_renewalTokens.Count > 0)
                    {
                        return;
                    }

                    observed = _parkedObservedSource.Task;
                }

                var first = await Task.WhenAny(observed, Task.Delay(timeout)).ConfigureAwait(false);
                if (!ReferenceEquals(first, observed))
                {
                    throw new TimeoutException("Expected a renewal to park on the injected delay, but none did.");
                }
            }
        }
    }
}
