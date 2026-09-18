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

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingMessageLockRenewalRegistry
{
    // Pins the PER-DELIVERY renewal lifetime a non-session receiver needs: one adapter serves N concurrent
    // in-flight messages, so renewal state is keyed per delivery rather than per adapter. Every fact is driven
    // through a delay that PARKS until the test releases it, on a clock that never moves, so no assertion sleeps
    // or depends on wall-clock timing.
    public class WhenRenewingMessageLocks : Testing.Core.Context
    {
        private const string _receiverPath = "message-queue";
        private static readonly DateTimeOffset _startOfTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

        private readonly ParkedDelayGate _delays = new ParkedDelayGate();

        [Fact]
        public void ItStartsARenewalLoopWithItsOwnCancellationPerDelivery()
        {
            var firstDelivery = ServiceBusMessageFactory.ReceivedMessage(messageId: "first");
            var secondDelivery = ServiceBusMessageFactory.ReceivedMessage(messageId: "second");
            var sut = CreateSut();

            sut.Start(firstDelivery, _ => Task.CompletedTask);
            sut.Start(secondDelivery, _ => Task.CompletedTask);

            sut.ActiveRenewalCount.Should().Be(2, "a non-session adapter serves concurrent deliveries, so each in-flight message renews on its own");
            _delays.RenewalTokens.Should().HaveCount(2, "two loops waiting on ONE shared cancellation would let either delivery's release end the other's renewal");
        }

        [Fact]
        public async Task ItRenewsEachConcurrentDeliveryIndependently()
        {
            var sut = CreateSut();
            var firstRenewed = new RenewalSignal();
            var secondRenewed = new RenewalSignal();
            var firstRenewalToken = StartRenewal(sut, "first", firstRenewed.RenewAsync);
            StartRenewal(sut, "second", secondRenewed.RenewAsync);

            _delays.Release(firstRenewalToken);

            (await CompletedWithin(firstRenewed.Renewed, _waitTimeout)).Should().BeTrue("the delivery whose renewal wait elapsed renews its own lock");
            secondRenewed.HasRenewed.Should().BeFalse("a concurrent delivery renews on its own cadence, not on a sibling's");
        }

        [Fact]
        public async Task ItEndsOnlyTheTargetedDeliverysRenewal()
        {
            var sut = CreateSut();
            var endedDelivery = ServiceBusMessageFactory.ReceivedMessage(messageId: "ended");
            var survivingRenewed = new RenewalSignal();
            var endedRenewalToken = StartRenewal(sut, endedDelivery, _ => Task.CompletedTask);
            var survivingRenewalToken = StartRenewal(sut, "surviving", survivingRenewed.RenewAsync);

            sut.Stop(endedDelivery);

            endedRenewalToken.IsCancellationRequested.Should().BeTrue("the settled delivery's own renewal ends with it");
            survivingRenewalToken.IsCancellationRequested.Should().BeFalse("one cancellation source per adapter would cancel the wrong delivery's renewal");
            sut.ActiveRenewalCount.Should().Be(1, "no registration survives a Stop");

            _delays.Release(survivingRenewalToken);

            (await CompletedWithin(survivingRenewed.Renewed, _waitTimeout)).Should().BeTrue("the still in-flight delivery keeps renewing after its sibling is released");
        }

        [Fact]
        public void ItIgnoresARepeatedStopForTheSameDelivery()
        {
            var sut = CreateSut();
            var delivery = ServiceBusMessageFactory.ReceivedMessage(messageId: "released-twice");
            StartRenewal(sut, delivery, _ => Task.CompletedTask);
            sut.Stop(delivery);

            Action repeatedStop = () => sut.Stop(delivery);

            repeatedStop.Should().NotThrow("release runs from the worker's finally, which a settlement recovery can reach more than once");
            sut.ActiveRenewalCount.Should().Be(0, "no registration survives a Stop");
        }

        [Fact]
        public void ItIgnoresAStopForADeliveryItNeverStarted()
        {
            var sut = CreateSut();

            Action stop = () => sut.Stop(ServiceBusMessageFactory.ReceivedMessage(messageId: "never-started"));

            stop.Should().NotThrow("a receiver rebuilt after an ObjectDisposedException routes a discarded adapter's in-flight release to an adapter that never saw that delivery, so throwing here would turn a normal recovery into a crash");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ItStartsNoRenewalWhenRenewalIsDisabled(int maxRenewalSeconds)
        {
            var sut = CreateSut(TimeSpan.FromSeconds(maxRenewalSeconds));

            sut.Start(ServiceBusMessageFactory.ReceivedMessage(messageId: "never-renewed"), _ => Task.CompletedTask);

            sut.ActiveRenewalCount.Should().Be(0, "a non-positive ceiling is renewal being off, and an off registry holds no delivery's renewal state");
            _delays.RenewalTokens.Should().BeEmpty("a disabled registry starts no loop at all rather than one that immediately stops");
        }

        [Fact]
        public void ItStartsExactlyOneRenewalForARepeatedStartOfTheSameDelivery()
        {
            var sut = CreateSut();
            var delivery = ServiceBusMessageFactory.ReceivedMessage(messageId: "delivered-once");
            StartRenewal(sut, delivery, _ => Task.CompletedTask);

            sut.Start(delivery, _ => Task.CompletedTask);

            sut.ActiveRenewalCount.Should().Be(1, "renewal is keyed by delivery, so one in-flight message reference holds one renewal");
            _delays.RenewalTokens.Should().HaveCount(1, "a second loop for the same delivery would keep renewing past the Stop that ends the first");
        }

        [Fact]
        public async Task ItEndsAndAwaitsEveryRenewalOnClose()
        {
            var sut = CreateSut();
            var firstRenewalToken = StartRenewal(sut, "first", _ => Task.CompletedTask);
            var secondRenewalToken = StartRenewal(sut, "second", _ => Task.CompletedTask);

            var close = sut.CloseAsync();

            firstRenewalToken.IsCancellationRequested.Should().BeTrue("closing ends every in-flight delivery's renewal");
            secondRenewalToken.IsCancellationRequested.Should().BeTrue("closing ends every in-flight delivery's renewal");
            close.IsCompleted.Should().BeFalse("a close that returned while a renewal loop was still running would leave a renewal outliving the receiver that owns it");
            sut.ActiveRenewalCount.Should().Be(0, "no registration survives a close");

            _delays.ReleaseAll();

            (await CompletedWithin(close, _waitTimeout)).Should().BeTrue("close completes once every renewal loop it cancelled has ended");
        }

        [Fact]
        public async Task ItIgnoresARepeatedClose()
        {
            var sut = CreateSut();
            StartRenewal(sut, "delivery", _ => Task.CompletedTask);
            var close = sut.CloseAsync();
            _delays.ReleaseAll();
            (await CompletedWithin(close, _waitTimeout)).Should().BeTrue("the first close ends the started renewal");

            Func<Task> repeatedClose = () => sut.CloseAsync();

            await repeatedClose.Should().NotThrowAsync("a receiver stopped twice must not fault on renewals it has already ended");
            sut.ActiveRenewalCount.Should().Be(0, "no registration survives a close");
        }

        [Fact]
        public async Task ItStartsNoRenewalOnceClosed()
        {
            var sut = CreateSut();
            await sut.CloseAsync();

            sut.Start(ServiceBusMessageFactory.ReceivedMessage(messageId: "late"), _ => Task.CompletedTask);

            sut.ActiveRenewalCount.Should().Be(0, "a renewal started after the close that awaits every loop would be one nothing ever ends");
            _delays.RenewalTokens.Should().BeEmpty("a closed registry starts no loop at all");
        }

        [Fact]
        public async Task ItStillAwaitsAStoppedDeliverysRenewalOnClose()
        {
            var sut = CreateSut();
            var delivery = ServiceBusMessageFactory.ReceivedMessage(messageId: "stopped-mid-renewal");
            var renewal = new ParkedRenewal();
            var renewalToken = StartRenewal(sut, delivery, renewal.RenewAsync);
            _delays.Release(renewalToken);
            (await CompletedWithin(renewal.Entered, _waitTimeout)).Should().BeTrue("the loop must be inside the broker's renew call before the delivery is stopped");

            sut.Stop(delivery);
            var close = sut.CloseAsync();

            close.IsCompleted.Should().BeFalse("a stopped delivery whose renewal is still awaiting the broker must remain visible to teardown, or close reports done and the adapter closes the SDK receiver under a live renewal");

            renewal.Complete();

            (await CompletedWithin(close, _waitTimeout)).Should().BeTrue("close completes once the stopped delivery's renewal has actually ended");
        }

        [Fact]
        public async Task ItSharesOneCompletionAcrossOverlappingCloses()
        {
            var sut = CreateSut();
            StartRenewal(sut, "renewing", _ => Task.CompletedTask);

            var firstClose = sut.CloseAsync();
            var secondClose = sut.CloseAsync();

            firstClose.IsCompleted.Should().BeFalse("close does not complete while a renewal loop it cancelled is still running");
            secondClose.IsCompleted.Should().BeFalse("a second close that returned early would let its caller close the SDK receiver while the first close is still awaiting a renewal");

            _delays.ReleaseAll();

            (await CompletedWithin(firstClose, _waitTimeout)).Should().BeTrue("close completes once every renewal loop it cancelled has ended");
            (await CompletedWithin(secondClose, _waitTimeout)).Should().BeTrue("every close of one registry shares the one completion");
        }

        [Fact]
        public async Task ItStopsARenewalWhoseCancellationCallbackThrows()
        {
            var sut = CreateSut();
            var delivery = ServiceBusMessageFactory.ReceivedMessage(messageId: "cancellation-throws");
            var renewalToken = StartRenewal(sut, delivery, _ => Task.CompletedTask);
            renewalToken.Register(() => throw new InvalidOperationException("a cancellation callback threw"));

            Action stop = () => sut.Stop(delivery);

            stop.Should().NotThrow("release runs from the worker's finally and must not throw, and Cancel raises whatever its callbacks raise");
            renewalToken.IsCancellationRequested.Should().BeTrue("Cancel signals the token before running the callback that throws, so the renewal still ends");
            sut.ActiveRenewalCount.Should().Be(0, "a stopped delivery is no longer in flight even when its cancellation callback failed");

            var close = sut.CloseAsync();
            _delays.ReleaseAll();

            (await CompletedWithin(close, _waitTimeout)).Should().BeTrue("a renewal whose cancellation failed must still be reachable by teardown, or every later close waits for a renewal nothing will ever report");
        }

        [Fact]
        public async Task ItEndsEveryRenewalOnCloseWhenACancellationCallbackThrows()
        {
            var sut = CreateSut();
            var firstRenewalToken = StartRenewal(sut, "first", _ => Task.CompletedTask);
            var secondRenewalToken = StartRenewal(sut, "second", _ => Task.CompletedTask);
            firstRenewalToken.Register(() => throw new InvalidOperationException("a cancellation callback threw"));

            var close = sut.CloseAsync();

            secondRenewalToken.IsCancellationRequested.Should().BeTrue("one delivery's failed cancellation must not leave a sibling renewing against the receiver about to close");
            var overlappingClose = sut.CloseAsync();
            _delays.ReleaseAll();

            (await CompletedWithin(close, _waitTimeout)).Should().BeTrue("close completes once every renewal it ended has ended, however the cancelling went");
            (await CompletedWithin(overlappingClose, _waitTimeout)).Should().BeTrue("a close that failed part way through cancelling must not leave later closes waiting forever");
        }

        [Fact]
        public async Task ItCompletesCloseWhenARenewalLoopFails()
        {
            var sut = CreateSut();
            var renewalToken = StartRenewal(sut, "failing", _ => throw new InvalidOperationException("the broker refused the renewal"));
            _delays.Release(renewalToken);

            var close = sut.CloseAsync();
            _delays.ReleaseAll();

            (await CompletedWithin(close, _waitTimeout)).Should().BeTrue("a renewal that failed has ended, which is all teardown waits for");
            close.IsFaulted.Should().BeFalse("a failed renewal is reported where it happened; raising it here would stop the adapter closing the receiver it renews against");
        }

        [Fact]
        public async Task ItStartsNoSecondRenewalForADeliveryWhoseStopIsStillEnding()
        {
            var sut = CreateSut();
            var delivery = ServiceBusMessageFactory.ReceivedMessage(messageId: "stopped-mid-renewal");
            var renewal = new ParkedRenewal();
            var renewalToken = StartRenewal(sut, delivery, renewal.RenewAsync);
            _delays.Release(renewalToken);
            (await CompletedWithin(renewal.Entered, _waitTimeout)).Should().BeTrue("the loop must be inside the broker's renew call before the delivery is stopped");
            sut.Stop(delivery);

            sut.Start(delivery, _ => Task.CompletedTask);

            _delays.RenewalTokens.Should().HaveCount(1, "a stopped delivery is still held until its renewal ends, so nothing can arm a second renewal against a delivery already released");
            sut.ActiveRenewalCount.Should().Be(0, "a stopped delivery is no longer in flight, however long its renewal takes to end");

            renewal.Complete();
        }

        private static async Task<bool> CompletedWithin(Task pending, TimeSpan timeout)
            => await Task.WhenAny(pending, Task.Delay(timeout)).ConfigureAwait(false) == pending;

        private CancellationToken StartRenewal(MessageLockRenewalRegistry sut, string messageId, Func<CancellationToken, Task> renewAsync)
            => StartRenewal(sut, ServiceBusMessageFactory.ReceivedMessage(messageId: messageId), renewAsync);

        // Answers the renewal token the started delivery's own loop parked on, which is how a test tells two
        // concurrent deliveries' loops apart.
        private CancellationToken StartRenewal(MessageLockRenewalRegistry sut, ServiceBusReceivedMessage delivery, Func<CancellationToken, Task> renewAsync)
        {
            var alreadyRenewing = _delays.RenewalTokens.Count;
            sut.Start(delivery, renewAsync);

            var renewalTokens = _delays.RenewalTokens;
            renewalTokens.Should().HaveCount(alreadyRenewing + 1, "a started delivery parks its loop on a renewal token of its own");
            return renewalTokens[alreadyRenewing];
        }

        private MessageLockRenewalRegistry CreateSut(TimeSpan? maxRenewalDuration = null)
            => new MessageLockRenewalRegistry(maxRenewalDuration ?? TimeSpan.FromHours(1),
                                              _receiverPath,
                                              Mock.Of<ILogger>(),
                                              new FixedTimeProvider(_startOfTime),
                                              _delays.DelayAsync);

        /// <summary>
        /// A <see cref="TimeProvider"/> whose UTC now never moves, so no loop reaches its renewal ceiling and every
        /// started loop stays observable for the duration of a test.
        /// </summary>
        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _utcNow;

            public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

            public override DateTimeOffset GetUtcNow() => _utcNow;
        }

        /// <summary>
        /// Stands in for <see cref="Task.Delay(TimeSpan, CancellationToken)"/>: it records the renewal token that
        /// asked to wait, then PARKS — the wait completes only when the test releases it. Parking is what
        /// keeps every started loop alive and independently steppable with no wall-clock sleep, and the renewal token
        /// is what identifies WHICH delivery's loop is waiting.
        /// </summary>
        private sealed class ParkedDelayGate
        {
            private readonly object _syncLock = new object();
            private readonly List<ParkedDelay> _parked = new List<ParkedDelay>();

            public Task DelayAsync(TimeSpan delay, CancellationToken renewalToken)
            {
                var parked = new ParkedDelay(renewalToken);
                lock (_syncLock)
                {
                    _parked.Add(parked);
                }

                return parked.Completion.Task;
            }

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

            /// <summary>Lets the loop waiting on <paramref name="renewalToken"/> take its next step.</summary>
            public void Release(CancellationToken renewalToken)
            {
                List<ParkedDelay> releasing;
                lock (_syncLock)
                {
                    releasing = _parked.Where(parked => parked.RenewalToken == renewalToken && !parked.Completion.Task.IsCompleted).ToList();
                }

                foreach (var parked in releasing)
                {
                    parked.Completion.TrySetResult(true);
                }
            }

            /// <summary>Lets every parked loop take its next step, which for a cancelled loop is to end.</summary>
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
        }

        /// <summary>
        /// One delivery's renew delegate, plus a bounded wait for it having been called. The wait is bounded rather
        /// than timed: the assertion is on whether the renewal happened at all, never on how long it took.
        /// </summary>
        private sealed class RenewalSignal
        {
            private readonly TaskCompletionSource<bool> _renewed = new TaskCompletionSource<bool>();

            public Task Renewed => _renewed.Task;

            public bool HasRenewed => _renewed.Task.IsCompleted;

            public Task RenewAsync(CancellationToken renewalToken)
            {
                _renewed.TrySetResult(true);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// One delivery's renew delegate that PARKS INSIDE the broker call until the test completes it. That is
        /// the state a renewal is in when a stop or a close races a renewal already in flight — the interleaving a
        /// renewal that only parks on its delay can never reach.
        /// </summary>
        private sealed class ParkedRenewal
        {
            private readonly TaskCompletionSource<bool> _entered = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>();

            /// <summary>Completes once the loop has actually entered the renew call and parked there.</summary>
            public Task Entered => _entered.Task;

            public Task RenewAsync(CancellationToken renewalToken)
            {
                _entered.TrySetResult(true);
                return _completion.Task;
            }

            public void Complete() => _completion.TrySetResult(true);
        }

        private sealed class ParkedDelay
        {
            public ParkedDelay(CancellationToken renewalToken) => RenewalToken = renewalToken;

            public CancellationToken RenewalToken { get; }

            public TaskCompletionSource<bool> Completion { get; } = new TaskCompletionSource<bool>();
        }
    }
}
