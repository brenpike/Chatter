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

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingLockRenewalLoop
{
    // Pins the bounded lock-renewal policy shared by the session and non-session receive paths. The loop takes
    // no Azure SDK receiver — a lock-expiry delegate, a renew delegate, a clock and a delay delegate — so every
    // fact below is driven on a hand-advanced clock with no wall-clock wait.
    public class WhenRenewingALock : Testing.Core.Context
    {
        private const string _description = "session 'abc' on 'session-queue'";
        private static readonly DateTimeOffset _startOfTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan _untilTheEndOfTime = DateTimeOffset.MaxValue - _startOfTime;

        [Fact]
        public async Task ItRenewsRepeatedlyUntilTheCeilingIsReached()
        {
            var clock = new AdvanceableTimeProvider(_startOfTime);
            var delays = new DelayRecorder(clock);
            var renewals = 0;

            var sut = CreateSut(clock,
                                delays,
                                lockedUntil: () => clock.GetUtcNow() + TimeSpan.FromSeconds(30),
                                renewAsync: _ =>
                                {
                                    renewals++;
                                    return Task.CompletedTask;
                                },
                                maxRenewalDuration: TimeSpan.FromMinutes(1));

            await sut.RunAsync(CancellationToken.None);

            renewals.Should().Be(3, "a 30 second lock renews at its halfway point, so a one minute ceiling admits renewals at 15, 30 and 45 seconds and stops at the ceiling");
        }

        [Fact]
        public async Task ItFloorsAShortComputedDelayAtOneSecond()
        {
            var clock = new AdvanceableTimeProvider(_startOfTime);
            var delays = new DelayRecorder(clock);

            var sut = CreateSut(clock,
                                delays,
                                lockedUntil: () => clock.GetUtcNow() + TimeSpan.FromSeconds(1),
                                renewAsync: _ => Task.CompletedTask,
                                maxRenewalDuration: TimeSpan.FromSeconds(3));

            await sut.RunAsync(CancellationToken.None);

            delays.Requested.Should().OnlyContain(delay => delay == TimeSpan.FromSeconds(1),
                                                  "half of a one second lock is below the floor, so every wait is the one second floor rather than 500 milliseconds of spinning");
        }

        [Fact]
        public async Task ItFloorsAnAlreadyExpiredLockAtOneSecondRatherThanWaitingANegativeSpan()
        {
            var clock = new AdvanceableTimeProvider(_startOfTime);
            var delays = new DelayRecorder(clock);

            var sut = CreateSut(clock,
                                delays,
                                lockedUntil: () => clock.GetUtcNow() - TimeSpan.FromSeconds(30),
                                renewAsync: _ => Task.CompletedTask,
                                maxRenewalDuration: TimeSpan.FromSeconds(2));

            await sut.RunAsync(CancellationToken.None);

            delays.Requested.Should().OnlyContain(delay => delay == TimeSpan.FromSeconds(1),
                                                  "an expired lock halves to a negative span, which must renew promptly at the floor instead");
        }

        [Fact]
        public async Task ItStopsPromptlyOnCancellationWithoutThrowing()
        {
            var clock = new AdvanceableTimeProvider(_startOfTime);
            var delays = new DelayRecorder(clock);
            var renewals = 0;

            using (var renewalCts = new CancellationTokenSource())
            {
                // Cancellation arrives while the loop is waiting out its renewal delay — the teardown path a
                // release cancels through.
                delays.CancelDuringDelay = renewalCts;

                var sut = CreateSut(clock,
                                    delays,
                                    lockedUntil: () => clock.GetUtcNow() + TimeSpan.FromSeconds(30),
                                    renewAsync: _ =>
                                    {
                                        renewals++;
                                        return Task.CompletedTask;
                                    },
                                    maxRenewalDuration: TimeSpan.FromHours(1));

                Func<Task> run = () => sut.RunAsync(renewalCts.Token);

                await run.Should().NotThrowAsync("a cancelled renewal is normal teardown, not a fault");
                delays.Requested.Should().HaveCount(1, "the loop stops on the cancellation it observed rather than waiting again");
            }

            renewals.Should().Be(0, "a loop cancelled mid-wait never reaches its renewal call");
        }

        [Fact]
        public async Task ItStopsWithoutThrowingWhenTheLockIsLost()
        {
            var clock = new AdvanceableTimeProvider(_startOfTime);
            var delays = new DelayRecorder(clock);

            var sut = CreateSut(clock,
                                delays,
                                lockedUntil: () => clock.GetUtcNow() + TimeSpan.FromSeconds(30),
                                renewAsync: _ => throw new ServiceBusException("lock lost", ServiceBusFailureReason.SessionLockLost),
                                maxRenewalDuration: TimeSpan.FromHours(1),
                                lockLostReason: ServiceBusFailureReason.SessionLockLost);

            Func<Task> run = () => sut.RunAsync(CancellationToken.None);

            await run.Should().NotThrowAsync("a lock lost out from under renewal stops the loop; the receive path observes the same loss and releases");
            delays.Requested.Should().HaveCount(1, "the loop stops at the lost lock rather than renewing again");
        }

        [Fact]
        public async Task ItStopsWithoutThrowingWhenTheReceiverWasDisposedConcurrently()
        {
            var clock = new AdvanceableTimeProvider(_startOfTime);
            var delays = new DelayRecorder(clock);

            var sut = CreateSut(clock,
                                delays,
                                lockedUntil: () => clock.GetUtcNow() + TimeSpan.FromSeconds(30),
                                renewAsync: _ => throw new ObjectDisposedException("receiver"),
                                maxRenewalDuration: TimeSpan.FromHours(1));

            Func<Task> run = () => sut.RunAsync(CancellationToken.None);

            await run.Should().NotThrowAsync("the release path owns teardown; a renewal racing a closed receiver stops rather than faulting");
        }

        [Fact]
        public async Task ItDoesNotSwallowAServiceBusFailureOtherThanLockLoss()
        {
            var clock = new AdvanceableTimeProvider(_startOfTime);
            var delays = new DelayRecorder(clock);
            var quotaExceeded = new ServiceBusException("quota exceeded", ServiceBusFailureReason.QuotaExceeded);

            var sut = CreateSut(clock,
                                delays,
                                lockedUntil: () => clock.GetUtcNow() + TimeSpan.FromSeconds(30),
                                renewAsync: _ => throw quotaExceeded,
                                maxRenewalDuration: TimeSpan.FromHours(1),
                                lockLostReason: ServiceBusFailureReason.SessionLockLost);

            Func<Task> run = () => sut.RunAsync(CancellationToken.None);

            (await run.Should().ThrowAsync<ServiceBusException>("only the lock-loss reason is an expected renewal outcome; any other broker failure must stay visible"))
                .Which.Should().BeSameAs(quotaExceeded);
        }

        [Fact]
        public async Task ItRenewsRatherThanFaultingUnderAnUnboundedMaxRenewalDuration()
        {
            var renewals = 0;
            Func<Task> run = async () => renewals = await RenewOnceUnderCeilingAsync(TimeSpan.MaxValue);

            await run.Should().NotThrowAsync("an unbounded duration must saturate the ceiling at the end of time rather than overflow the clock, which would leave renewal silently off while it is configured on");
            renewals.Should().Be(1, "a saturated ceiling is never reached, so renewal runs for as long as the delivery does");
        }

        [Fact]
        public async Task ItRenewsAtTheLargestDurationTheClockCanRepresent()
        {
            var renewals = 0;
            Func<Task> run = async () => renewals = await RenewOnceUnderCeilingAsync(_untilTheEndOfTime);

            await run.Should().NotThrowAsync("a duration that lands exactly on the end of time is representable and must renew");
            renewals.Should().Be(1, "a ceiling at the end of time is never reached, so renewal runs for as long as the delivery does");
        }

        [Fact]
        public async Task ItRenewsRatherThanFaultingOneTickBeyondTheEndOfTime()
        {
            var renewals = 0;
            Func<Task> run = async () => renewals = await RenewOnceUnderCeilingAsync(_untilTheEndOfTime + TimeSpan.FromTicks(1));

            await run.Should().NotThrowAsync("the smallest duration that overruns the clock saturates at the end of time rather than faulting the loop before it starts");
            renewals.Should().Be(1, "a saturated ceiling is never reached, so renewal runs for as long as the delivery does");
        }

        [Fact]
        public void ItReportsRenewalDisabledForTheMostNegativeMaxRenewalDuration()
            => LockRenewalLoop.IsEnabled(TimeSpan.MinValue)
                              .Should().BeFalse("the disabled check is a pure comparison, so the most negative duration is answered without any clock arithmetic to overflow");

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ItReportsRenewalDisabledForANonPositiveMaxRenewalDuration(int maxRenewalSeconds)
            => LockRenewalLoop.IsEnabled(TimeSpan.FromSeconds(maxRenewalSeconds))
                              .Should().BeFalse("a non-positive ceiling is the single definition of renewal being off");

        [Fact]
        public void ItReportsRenewalEnabledForAPositiveMaxRenewalDuration()
            => LockRenewalLoop.IsEnabled(TimeSpan.FromSeconds(1))
                              .Should().BeTrue("any positive ceiling admits at least one renewal window");

        /// <summary>
        /// Runs a loop whose ceiling is <paramref name="maxRenewalDuration"/> until its first renewal, which cancels
        /// the loop: a ceiling at or beyond the end of time is never reached, so the renewal itself is what stops it.
        /// </summary>
        private static async Task<int> RenewOnceUnderCeilingAsync(TimeSpan maxRenewalDuration)
        {
            var clock = new AdvanceableTimeProvider(_startOfTime);
            var delays = new DelayRecorder(clock);
            var renewals = 0;

            using (var renewalCts = new CancellationTokenSource())
            {
                var sut = CreateSut(clock,
                                    delays,
                                    lockedUntil: () => clock.GetUtcNow() + TimeSpan.FromSeconds(30),
                                    renewAsync: _ =>
                                    {
                                        renewals++;
                                        renewalCts.Cancel();
                                        return Task.CompletedTask;
                                    },
                                    maxRenewalDuration: maxRenewalDuration);

                await sut.RunAsync(renewalCts.Token);
            }

            return renewals;
        }

        private static LockRenewalLoop CreateSut(AdvanceableTimeProvider clock,
                                                 DelayRecorder delays,
                                                 Func<DateTimeOffset> lockedUntil,
                                                 Func<CancellationToken, Task> renewAsync,
                                                 TimeSpan maxRenewalDuration,
                                                 ServiceBusFailureReason lockLostReason = ServiceBusFailureReason.SessionLockLost,
                                                 ILogger logger = null)
            => new LockRenewalLoop(lockedUntil,
                                   renewAsync,
                                   maxRenewalDuration,
                                   lockLostReason,
                                   _description,
                                   logger ?? Mock.Of<ILogger>(),
                                   clock,
                                   delays.DelayAsync);

        /// <summary>
        /// A <see cref="TimeProvider"/> whose UTC now only moves when a test moves it, so the renewal ceiling and
        /// cadence are exercised with no wall-clock sleep.
        /// </summary>
        private sealed class AdvanceableTimeProvider : TimeProvider
        {
            private DateTimeOffset _utcNow;

            public AdvanceableTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

            public override DateTimeOffset GetUtcNow() => _utcNow;

            public void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
        }

        /// <summary>
        /// Stands in for <see cref="Task.Delay(TimeSpan, CancellationToken)"/>: it records what the loop asked to
        /// wait, advances the clock by that much, and completes immediately.
        /// </summary>
        private sealed class DelayRecorder
        {
            private readonly AdvanceableTimeProvider _clock;

            public DelayRecorder(AdvanceableTimeProvider clock) => _clock = clock;

            public List<TimeSpan> Requested { get; } = new List<TimeSpan>();

            /// <summary>When set, the wait is cancelled from under the loop the first time it waits.</summary>
            public CancellationTokenSource CancelDuringDelay { get; set; }

            // INVARIANT: a non-positive wait would never move the clock toward the ceiling, so the loop would spin
            // forever; the cap turns that regression into a failed assertion instead of a hung test run.
            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                Requested.Add(delay);
                Requested.Count.Should().BeLessThan(50, "the renewal loop must reach its ceiling rather than spin");

                CancelDuringDelay?.Cancel();

                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled(cancellationToken);
                }

                _clock.Advance(delay);
                return Task.CompletedTask;
            }
        }
    }
}
