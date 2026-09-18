using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingRenewalLifetime
{
    // Pins the ownership a renewal lifetime takes off its registry: ONE object holds one renewal's cancellation
    // source, its loop, its exit from the registry's tracking and its fault report, so no caller can skip a
    // cleanup by throwing and no caller can be left holding a completion nothing will complete. Every fact is
    // driven through a loop the test parks and resumes, so no assertion sleeps.
    public class WhenOwningARenewal : Testing.Core.Context
    {
        private const string _description = "message 'delivered' on 'message-queue'";
        private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ItCompletesRatherThanFaultsWhenItsRenewalLoopFails()
        {
            var sut = CreateSut();

            sut.Begin(_ => Task.FromException(new InvalidOperationException("the broker refused the renewal")), () => { });

            (await CompletedWithin(sut.Completion, _waitTimeout)).Should().BeTrue("a renewal that failed still ended, and teardown awaits this completion");
            sut.Completion.IsFaulted.Should().BeFalse("a failed renewal is reported by its owner, not raised at the teardown awaiting it");
        }

        [Fact]
        public async Task ItLeavesItsTrackingBeforeItsCompletionCompletes()
        {
            var sut = CreateSut();
            var tracked = true;

            sut.Begin(_ => Task.CompletedTask, () => tracked = false);
            (await CompletedWithin(sut.Completion, _waitTimeout)).Should().BeTrue("the renewal ends once its loop returns");

            tracked.Should().BeFalse("a teardown that observes this completion must never find the ended renewal still recorded as running");
        }

        [Fact]
        public async Task ItLeavesItsTrackingExactlyOnceWhenItsLoopFails()
        {
            var sut = CreateSut();
            var exits = 0;

            sut.Begin(_ => Task.FromException(new InvalidOperationException("the broker refused the renewal")), () => exits++);
            await CompletedWithin(sut.Completion, _waitTimeout);

            exits.Should().Be(1, "the exit is owed exactly once however the renewal ended, since a second exit could remove a later renewal for the same delivery");
        }

        [Fact]
        public void ItLeavesItsTrackingInsideBeginWhenItsLoopEndsSynchronously()
        {
            var sut = CreateSut();
            var exits = 0;

            sut.Begin(_ => Task.CompletedTask, () => exits++);

            exits.Should().Be(1, "a loop that never awaits ends inside Begin, which is why a registry must record the renewal BEFORE beginning it");
        }

        [Fact]
        public async Task ItSignalsTheRenewalTokenWhenACancellationCallbackThrows()
        {
            var sut = CreateSut();
            var parkedLoop = new ParkedLoop();
            sut.Begin(parkedLoop.RunAsync, () => { });
            (await CompletedWithin(parkedLoop.Running, _waitTimeout)).Should().BeTrue("the loop must hold its renewal token before the cancellation callback is registered");
            parkedLoop.RenewalToken.Register(() => throw new InvalidOperationException("a cancellation callback threw"));

            Action end = () => sut.End();

            end.Should().NotThrow("Cancel raises whatever its callbacks raise, and the release path that ends a renewal must not throw");
            parkedLoop.RenewalToken.IsCancellationRequested.Should().BeTrue("Cancel signals the token before running the callback that throws, so the loop still ends");

            parkedLoop.Complete();
            (await CompletedWithin(sut.Completion, _waitTimeout)).Should().BeTrue("the renewal still ends after a cancellation callback failed");
        }

        [Fact]
        public async Task ItIgnoresAnEndAfterTheRenewalHasEnded()
        {
            var sut = CreateSut();
            sut.Begin(_ => Task.CompletedTask, () => { });
            (await CompletedWithin(sut.Completion, _waitTimeout)).Should().BeTrue("the renewal ends on its own once its loop returns");

            Action end = () => sut.End();

            end.Should().NotThrow("a renewal that already ended disposed the source it would cancel, and both the release and the close end renewals they may have already ended");
        }

        [Fact]
        public async Task ItEndsWithoutThrowingWhenItsDiagnosticSinkFails()
        {
            var sut = CreateSut(new ThrowingLogger());
            var parkedLoop = new ParkedLoop();
            sut.Begin(parkedLoop.RunAsync, () => { });
            (await CompletedWithin(parkedLoop.Running, _waitTimeout)).Should().BeTrue("the loop must hold its renewal token before the cancellation callback is registered");
            parkedLoop.RenewalToken.Register(() => throw new InvalidOperationException("a cancellation callback threw"));

            Action end = () => sut.End();

            end.Should().NotThrow("reporting a failed cancellation through a broken sink must not make End throw — the delivery release calls it unguarded, and the close calls it in a loop that would skip every later renewal");
            parkedLoop.RenewalToken.IsCancellationRequested.Should().BeTrue("Cancel signals the token before the callback that throws, so the renewal still ends");

            parkedLoop.Complete();
            (await CompletedWithin(sut.Completion, _waitTimeout)).Should().BeTrue("the renewal still ends after both its cancellation and its sink failed");
        }

        [Fact]
        public async Task ItCompletesRatherThanFaultsWhenItsDiagnosticSinkFailsReportingAFailedRenewal()
        {
            var sut = CreateSut(new ThrowingLogger());

            sut.Begin(_ => Task.FromException(new InvalidOperationException("the broker refused the renewal")), () => { });

            (await CompletedWithin(sut.Completion, _waitTimeout)).Should().BeTrue("a renewal whose failure could not be reported has still ended, which is all a teardown waits for");
            sut.Completion.IsFaulted.Should().BeFalse("a broken sink must not fault the completion a close awaits, or the close fails, every later close observes the same faulted task, and the receiver this renewal runs against is never closed");
        }

        private static async Task<bool> CompletedWithin(Task pending, TimeSpan timeout)
            => await Task.WhenAny(pending, Task.Delay(timeout)).ConfigureAwait(false) == pending;

        private static RenewalLifetime CreateSut() => CreateSut(Mock.Of<ILogger>());

        private static RenewalLifetime CreateSut(ILogger logger) => new RenewalLifetime(_description, logger);

        /// <summary>An <see cref="ILogger"/> whose sink is broken, as a misconfigured application's can be.</summary>
        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) => throw new InvalidOperationException("the logging sink is broken");

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => throw new InvalidOperationException("the logging sink is broken");
        }

        /// <summary>
        /// A renewal loop that answers the renewal token it was handed and PARKS until the test completes it —
        /// the state a renewal is in when something ends it.
        /// </summary>
        private sealed class ParkedLoop
        {
            private readonly TaskCompletionSource<bool> _running = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>();

            /// <summary>Completes once the loop is running and its renewal token is readable.</summary>
            public Task Running => _running.Task;

            public CancellationToken RenewalToken { get; private set; }

            public Task RunAsync(CancellationToken renewalToken)
            {
                RenewalToken = renewalToken;
                _running.TrySetResult(true);
                return _completion.Task;
            }

            public void Complete() => _completion.TrySetResult(true);
        }
    }
}
