using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingSessionMessageReceiverAdapter
{
    // Pins the ONE renewal a held session owns: that it is recorded before it begins, that a release which races
    // the acquire cannot reach a renewal the adapter has not recorded, that a release is TOTAL however the renewal
    // ended, and that renewal being off is not a reason to refuse the session. Every fact is driven through a delay
    // that PARKS until the test releases it, on a clock that never moves, so no assertion sleeps or depends on
    // wall-clock timing.
    public class WhenOwningTheSessionRenewal : Testing.Core.Context
    {
        private const string _receiverPath = "session-queue";
        private static readonly DateTimeOffset _startOfTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan _sessionIdleTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan _renewalCeiling = TimeSpan.FromHours(1);
        private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

        private readonly ParkedDelayGate _delays = new ParkedDelayGate();
        private readonly InstrumentedHeldSession _session = new InstrumentedHeldSession("session-a");

        [Fact]
        public async Task MustSurviveAReleaseThatRacesTheAcquireItIsReleasing()
        {
            // The interleaving #500 reports: a release reaches the just-accepted session WHILE the acquire is still
            // arming that session's renewal. A renewal recorded only AFTER it begins leaves the release holding a
            // cancellation it disposes out from under an acquire that has not finished reading it.
            var sut = CreateSut(_renewalCeiling);
            _session.OnSessionIdRead(() => sut.CloseAsync().GetAwaiter().GetResult());

            Exception escaped = null;
            ServiceBusReceivedMessage received = null;
            try
            {
                received = await sut.ReceiveAsync(CancellationToken.None);
            }
            catch (Exception failure)
            {
                escaped = failure;
            }

            escaped.Should().BeNull("a release racing the acquire is normal teardown, not a fault the pump must stop on");
            received.Should().BeNull("a session accepted into a closing adapter is not served");
            _session.CloseCount.Should().Be(1, "the session accepted into the race is closed exactly once rather than leaked to its lock expiry");
            sut.HeldSessionId.Should().BeNull("a released adapter holds no session");
            _delays.RenewalTokens.Should().BeEmpty("a session the adapter never held renews nothing, so nothing is left running past the release");
        }

        [Fact]
        public async Task MustCloseTheHeldSessionWhenItsRenewalFailedUnrecognised()
        {
            // #499: the renewal policy completes without throwing for its three expected outcomes and FAULTS for
            // every other one. A release that only recognises the expected outcomes skips the close, leaking the
            // AMQP link and holding the session lock to expiry.
            var sut = CreateSut(_renewalCeiling);
            var renewal = new ParkedRenewal();
            _session.RenewalHandler = renewal.RenewAsync;
            await ReceiveOneMessageAsync(sut);
            await _delays.WaitForParkedDelayAsync(_waitTimeout);
            _delays.ReleaseAll();
            (await CompletedWithin(renewal.Entered, _waitTimeout)).Should().BeTrue("the loop must be inside the broker's renew call before that renewal is failed");

            renewal.Fail(new InvalidOperationException("the broker refused the session lock renewal"));

            Exception escaped = null;
            try
            {
                await sut.CloseAsync();
            }
            catch (Exception failure)
            {
                escaped = failure;
            }

            escaped.Should().BeNull("a renewal that failed is reported where it happened; raising it here would stop the adapter closing the session it renews against");
            _session.CloseCount.Should().Be(1, "a renewal failing an unrecognised way must not leave the session receiver open and its lock held to expiry");
            sut.HeldSessionId.Should().BeNull("a released adapter holds no session");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task MustHoldAnAcceptedSessionWhenRenewalIsDisabled(int maxRenewalSeconds)
        {
            // The canary. A non-positive ceiling is renewal being OFF, which gates the LOOP and nothing else:
            // reading "no renewal" as "this acquire raced a close" would silently close every session accepted by
            // a receiver configured not to renew.
            var delivered = AnyMessage();
            _session.YieldImmediately(delivered);
            var sut = CreateSut(TimeSpan.FromSeconds(maxRenewalSeconds));

            var received = await sut.ReceiveAsync(CancellationToken.None);

            received.Should().BeSameAs(delivered, "renewal being off is not a reason to refuse the session a receiver just accepted");
            sut.HeldSessionId.Should().Be("session-a", "the accepted session is held and served exactly as a renewing one is");
            _session.CloseCount.Should().Be(0, "a session accepted by a receiver that does not renew is held, never closed on acceptance");
            _delays.RenewalTokens.Should().BeEmpty("a disabled ceiling starts no loop at all rather than one that immediately stops");
        }

        [Fact]
        public async Task MustCloseASessionAcceptedIntoAnAlreadyClosedAdapter()
        {
            var delivered = AnyMessage();
            _session.YieldImmediately(delivered);
            var sut = CreateSut(_renewalCeiling);
            await sut.CloseAsync();

            var received = await sut.ReceiveAsync(CancellationToken.None);

            received.Should().BeNull("a session accepted after the adapter closed is not served; the pump re-polls");
            _session.CloseCount.Should().Be(1, "the accepted session is closed exactly once rather than left holding its lock");
            sut.HeldSessionId.Should().BeNull("a closed adapter holds no session");
            _delays.RenewalTokens.Should().BeEmpty("nothing renews a session the adapter refused to hold");
        }

        [Fact]
        public async Task MustCloseTheHeldSessionExactlyOnceWhenADrainedSessionIsAlsoClosed()
        {
            var sut = CreateSut(_renewalCeiling);
            var receiving = sut.ReceiveAsync(CancellationToken.None);
            await _session.WaitForReceiveCountAsync(1, _waitTimeout);
            _session.YieldNoMessage();
            (await receiving).Should().BeNull("a drained or idle session rolls: the pump re-polls and the adapter accepts the next session");

            await sut.CloseAsync();

            _session.CloseCount.Should().Be(1, "release is idempotent: a session already released by the drain is not closed a second time by the adapter's close");
        }

        [Fact]
        public async Task MustCloseTheHeldSessionExactlyOnceWhenALockLostSessionIsAlsoClosed()
        {
            var sut = CreateSut(_renewalCeiling);
            var receiving = sut.ReceiveAsync(CancellationToken.None);
            await _session.WaitForReceiveCountAsync(1, _waitTimeout);
            _session.FailReceive(new ServiceBusException("session lock lost", ServiceBusFailureReason.SessionLockLost));
            (await receiving).Should().BeNull("losing a session lock releases the session and re-polls rather than stopping the receiver");

            await sut.CloseAsync();

            _session.CloseCount.Should().Be(1, "release is idempotent: a session already released on lock loss is not closed a second time by the adapter's close");
        }

        [Fact]
        public async Task MustCloseTheHeldSessionExactlyOnceAcrossRepeatedCloses()
        {
            var sut = CreateSut(_renewalCeiling);
            await ReceiveOneMessageAsync(sut);

            await sut.CloseAsync();
            await sut.CloseAsync();

            _session.CloseCount.Should().Be(1, "close runs from teardown, which a receiver stopping and disposing both reach");
        }

        [Fact]
        public async Task MustAwaitARenewalStillInsideTheBrokerCallBeforeClosingTheSession()
        {
            var sut = CreateSut(_renewalCeiling);
            var renewal = new ParkedRenewal();
            _session.RenewalHandler = renewal.RenewAsync;
            await ReceiveOneMessageAsync(sut);
            await _delays.WaitForParkedDelayAsync(_waitTimeout);
            _delays.ReleaseAll();
            (await CompletedWithin(renewal.Entered, _waitTimeout)).Should().BeTrue("the loop must be inside the broker's renew call before the session is released");

            var closing = sut.CloseAsync();

            closing.IsCompleted.Should().BeFalse("a release that returns while a renewal is still awaiting the broker closes the session receiver under a live renewal");
            _session.CloseCount.Should().Be(0, "the session receiver may not close while a renewal call is still in flight against it");

            renewal.Complete();

            (await CompletedWithin(closing, _waitTimeout)).Should().BeTrue("release completes once the renewal it ended has actually ended");
            _session.CloseCount.Should().Be(1);
        }

        private static async Task<bool> CompletedWithin(Task pending, TimeSpan timeout)
            => await Task.WhenAny(pending, Task.Delay(timeout)).ConfigureAwait(false) == pending;

        private async Task ReceiveOneMessageAsync(AzureSdkSessionMessageReceiverAdapter sut, ServiceBusReceivedMessage message = null)
        {
            var alreadyReceived = _session.ReceiveCount;
            var receiving = sut.ReceiveAsync(CancellationToken.None);
            await _session.WaitForReceiveCountAsync(alreadyReceived + 1, _waitTimeout);
            _session.YieldMessage(message ?? AnyMessage());
            await receiving;
        }

        private Task<IServiceBusHeldSession> AcceptNextSessionAsync(CancellationToken cancellationToken)
            => Task.FromResult<IServiceBusHeldSession>(_session);

        private AzureSdkSessionMessageReceiverAdapter CreateSut(TimeSpan maxSessionLockRenewalDuration)
            => new AzureSdkSessionMessageReceiverAdapter(ServiceBusSessionEntityPath.Create(string.Empty, _receiverPath),
                                                         ServiceBusReceiveMode.PeekLock,
                                                         _sessionIdleTimeout,
                                                         maxSessionLockRenewalDuration,
                                                         Mock.Of<ILogger>(),
                                                         AcceptNextSessionAsync,
                                                         new FixedTimeProvider(_startOfTime),
                                                         _delays.DelayAsync);

        private static ServiceBusReceivedMessage AnyMessage() => ServiceBusMessageFactory.ReceivedMessage(sessionId: "session-a");

        /// <summary>
        /// Wraps <see cref="InMemoryHeldSession"/> with exactly the three hooks this class's facts need: a renewal
        /// the test drives, a session-id read the test interleaves a release into, and a receive that answers
        /// immediately for the facts that assert on acquisition alone.
        /// </summary>
        /// <remarks>
        /// The session-id read is the hook that reaches the acquire BETWEEN accepting a session and recording its
        /// renewal, because that read is what names the renewal being armed. It is the only seam a test has into
        /// that window, and the window is exactly where the released-before-recorded race lives.
        /// </remarks>
        private sealed class InstrumentedHeldSession : IServiceBusHeldSession
        {
            private readonly InMemoryHeldSession _session;
            private ServiceBusReceivedMessage _immediateAnswer;
            private Action _interleaved;

            public InstrumentedHeldSession(string sessionId) => _session = new InMemoryHeldSession(sessionId);

            public Func<CancellationToken, Task> RenewalHandler { get; set; }

            public int CloseCount => _session.CloseCount;

            public int ReceiveCount => _session.ReceiveCount;

            public string SessionId
            {
                get
                {
                    RunInterleavedAction();
                    return _session.SessionId;
                }
            }

            public DateTimeOffset SessionLockedUntil => _session.SessionLockedUntil;

            public bool IsClosed => _session.IsClosed;

            public ServiceBusSessionReceiver SdkSessionReceiver => _session.SdkSessionReceiver;

            /// <summary>Runs <paramref name="interleaved"/> the FIRST time the adapter reads this session's id.</summary>
            public void OnSessionIdRead(Action interleaved) => _interleaved = interleaved;

            /// <summary>Answers <paramref name="message"/> from every receive instead of parking on the test.</summary>
            public void YieldImmediately(ServiceBusReceivedMessage message) => _immediateAnswer = message;

            public Task<ServiceBusReceivedMessage> ReceiveMessageAsync(TimeSpan maxWaitTime, CancellationToken cancellationToken)
                => _immediateAnswer != null
                    ? Task.FromResult(_immediateAnswer)
                    : _session.ReceiveMessageAsync(maxWaitTime, cancellationToken);

            public Task CompleteMessageAsync(ServiceBusReceivedMessage message) => _session.CompleteMessageAsync(message);

            public Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
                => _session.AbandonMessageAsync(message, propertiesToModify);

            public Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
                => _session.DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription);

            public Task RenewSessionLockAsync(CancellationToken cancellationToken)
                => RenewalHandler != null ? RenewalHandler(cancellationToken) : _session.RenewSessionLockAsync(cancellationToken);

            public Task CloseAsync() => _session.CloseAsync();

            public Task WaitForReceiveCountAsync(int expectedCount, TimeSpan timeout) => _session.WaitForReceiveCountAsync(expectedCount, timeout);

            public void YieldMessage(ServiceBusReceivedMessage message) => _session.YieldMessage(message);

            public void YieldNoMessage() => _session.YieldNoMessage();

            public void FailReceive(Exception exception) => _session.FailReceive(exception);

            // Taken before it runs, so a read the interleaved action itself provokes cannot run it a second time.
            private void RunInterleavedAction()
            {
                var interleaved = _interleaved;
                _interleaved = null;
                interleaved?.Invoke();
            }
        }

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
        /// asked to wait, then PARKS — the wait completes when the test releases it, or cancels when that renewal
        /// ends. Parking is what keeps the started renewal alive and steppable with no wall-clock sleep.
        /// </summary>
        private sealed class ParkedDelayGate
        {
            private readonly object _syncLock = new object();
            private readonly List<ParkedDelay> _parked = new List<ParkedDelay>();
            private TaskCompletionSource<bool> _parkedObservedSource
                = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>The distinct renewal tokens that have asked to wait — one per running loop.</summary>
            public IReadOnlyList<CancellationToken> RenewalTokens
            {
                get
                {
                    lock (_syncLock)
                    {
                        return _parked.Select(parked => parked.RenewalToken).Distinct().ToList();
                    }
                }
            }

            public Task DelayAsync(TimeSpan delay, CancellationToken renewalToken)
            {
                var parked = new ParkedDelay(renewalToken);
                TaskCompletionSource<bool> observed;
                lock (_syncLock)
                {
                    _parked.Add(parked);
                    observed = _parkedObservedSource;
                    _parkedObservedSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                renewalToken.Register(() => parked.Completion.TrySetCanceled(renewalToken));
                observed.TrySetResult(true);
                return parked.Completion.Task;
            }

            /// <summary>Lets every parked renewal take its next step, which is to renew.</summary>
            public void ReleaseAll()
            {
                List<ParkedDelay> releasing;
                lock (_syncLock)
                {
                    releasing = _parked.Where(parked => !parked.Completion.Task.IsCompleted).ToList();
                }

                foreach (var parked in releasing)
                {
                    parked.Completion.TrySetResult(true);
                }
            }

            // Waits until a renewal has actually parked on this gate, without polling or sleeping. Throws rather
            // than hanging when none ever does.
            public async Task WaitForParkedDelayAsync(TimeSpan timeout)
            {
                Task observed;
                lock (_syncLock)
                {
                    if (_parked.Count > 0)
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

        /// <summary>
        /// The held session's renew delegate, PARKED INSIDE the broker call until the test completes or fails it.
        /// That is the state a renewal is in when a release races a renewal already in flight — the interleaving a
        /// renewal that only parks on its delay can never reach.
        /// </summary>
        private sealed class ParkedRenewal
        {
            private readonly TaskCompletionSource<bool> _entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Completes once the loop has actually entered the renew call and parked there.</summary>
            public Task Entered => _entered.Task;

            public Task RenewAsync(CancellationToken renewalToken)
            {
                _entered.TrySetResult(true);
                return _completion.Task;
            }

            public void Complete() => _completion.TrySetResult(true);

            /// <summary>Fails this renewal with <paramref name="failure"/>, which the renewal policy does not recognise.</summary>
            public void Fail(Exception failure) => _completion.TrySetException(failure);
        }

        private sealed class ParkedDelay
        {
            public ParkedDelay(CancellationToken renewalToken) => RenewalToken = renewalToken;

            public CancellationToken RenewalToken { get; }

            public TaskCompletionSource<bool> Completion { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
