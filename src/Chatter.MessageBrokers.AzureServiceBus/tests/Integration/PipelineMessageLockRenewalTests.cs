using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // End-to-end proof for issue #373: a handler that OUTLIVES the entity's lock duration no longer loses the
    // message lock, because Chatter renews it for the life of the delivery.
    //
    // The defect's observable signature, which these facts are shaped around: without renewal a slow handler's
    // lock expires mid-handling, the broker REDELIVERS the message while that handler is still running, every
    // redelivery increments DeliveryCount, and a healthy, successfully-handled message is walked to the
    // entity's MaxDeliveryCount and DEAD-LETTERED. "No exception was thrown" is NOT the assertion.
    //
    // The queue is dedicated (ServiceBusEmulatorFixture.LockRenewalQueue) and its Config.json entry pairs
    // LockDuration PT10S with MaxDeliveryCount 2, so the defect signature arrives within ~20 seconds of lock
    // loss rather than making a regressed run hang.
    //
    // Two facts, and the pair is the point:
    //   * RenewalDisabled... is the CONTROL. With renewal switched off the message IS dead-lettered, which
    //     proves the emulator really does expire an unrenewed lock — without it, the positive fact below could
    //     pass vacuously on a broker that never expires locks at all.
    //   * SlowHandler... is the PROOF. With renewal on (Chatter's default) the handler runs exactly once, the
    //     delivery is settled exactly once, and nothing reaches the dead-letter queue.
    //
    // Raw Azure.Messaging.ServiceBus appears ONLY at the test EDGE, to drain the queue and to read the
    // dead-letter sub-queue that Chatter does not expose for reads — never as the system under test. The send
    // is through IBrokeredMessageDispatcher and the receive/handle/settle is Chatter's own pump.
    //
    // Both facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineMessageLockRenewalTests
    {
        private const string LockRenewalQueue = ServiceBusEmulatorFixture.LockRenewalQueue;

        // Mirrors the queue's Config.json LockDuration. Duplicated here because the emulator config is data,
        // not a readable broker property, and the fact's timings are derived from it.
        private static readonly TimeSpan QueueLockDuration = TimeSpan.FromSeconds(10);

        // How long the handler holds the delivery. Comfortably past QueueLockDuration — that overshoot IS the
        // scenario, so it is wall-clock by nature and cannot be signalled away.
        private static readonly TimeSpan SlowHandlerDuration = TimeSpan.FromSeconds(25);

        // Bounded wait for the slow handler to finish: the handler's own duration plus generous headroom for
        // the emulator, finite so a stalled receive fails fast instead of hanging CI.
        private static readonly TimeSpan HandlerCompletionWait = SlowHandlerDuration + TimeSpan.FromSeconds(60);

        // The window watched AFTER the handler completes for a second invocation. Longer than the lock
        // duration, so a delivery whose lock was lost has had time to be redelivered and observed. Also the
        // window in which Chatter's settle resolves, so the dead-letter read below never looks too early.
        private static readonly TimeSpan RedeliveryWindow = QueueLockDuration + TimeSpan.FromSeconds(15);

        // Bounded read of the main queue / dead-letter sub-queue once the delivery has settled.
        private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(10);

        // Bounded wait for the CONTROL's dead-letter to land: the message must expire its lock twice (two lock
        // durations) and the pump must observe each redelivery behind the still-running handler, so the window
        // is several handler durations wide. Finite so a broker that never expires locks fails the control fast
        // and loudly rather than hanging.
        private static readonly TimeSpan ControlDeadLetterWait = TimeSpan.FromSeconds(150);

        // Per-attempt receive timeout used when clearing leftovers before a fact runs.
        private static readonly TimeSpan PurgeReceiveWait = TimeSpan.FromSeconds(2);

        // Upper bound on purge iterations so a pathological backlog cannot spin the drain forever.
        private const int MaxPurgeIterations = 20;

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineMessageLockRenewalTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        public sealed class LockRenewalCommand : ICommand
        {
            public string Value { get; set; }
        }

        // Shared, test-owned record of what Chatter's pipeline did to the slow handler: how many times it was
        // entered, how many times it ran to completion, and a signal for the first completion. Registered as a
        // singleton so the single instance is visible to both the test and the transiently-resolved handler.
        private sealed class SlowHandlerCoordinator
        {
            private readonly TimeSpan _handlerDuration;
            private readonly TaskCompletionSource<bool> _firstCompletion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _invocationCount;
            private int _completionCount;

            public SlowHandlerCoordinator(TimeSpan handlerDuration)
                => _handlerDuration = handlerDuration;

            // The number of times Chatter's pipeline ENTERED the handler. A lost lock shows up here as a second
            // entry while the first is still running.
            public int InvocationCount => Volatile.Read(ref _invocationCount);

            // The number of handler invocations that ran to completion without being cancelled.
            public int CompletionCount => Volatile.Read(ref _completionCount);

            public Task FirstCompletion => _firstCompletion.Task;

            public async Task HoldDeliveryAsync()
            {
                Interlocked.Increment(ref _invocationCount);
                await Task.Delay(_handlerDuration).ConfigureAwait(false);
                Interlocked.Increment(ref _completionCount);
                _firstCompletion.TrySetResult(true);
            }

            // Bounded wait for the first handler completion. Returns false on timeout so the caller asserts
            // with a diagnostic message instead of the run hanging.
            public async Task<bool> WaitForFirstCompletionAsync(TimeSpan timeout)
            {
                var completed = await Task.WhenAny(FirstCompletion, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == FirstCompletion;
            }

            // Bounded poll until the handler has been entered at least minCount times, returning the observed
            // count (which may be below minCount when the window elapses — the caller asserts on it).
            public async Task<int> WaitForInvocationCountAsync(int minCount, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (InvocationCount >= minCount)
                    {
                        return InvocationCount;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }

                return InvocationCount;
            }
        }

        // The handler Chatter resolves on the receive path. It does nothing but hold the delivery for longer
        // than the entity's lock duration, which is the entire scenario: the lock must survive the handler.
        private sealed class SlowMessageHandler : IMessageHandler<LockRenewalCommand>
        {
            private readonly SlowHandlerCoordinator _coordinator;

            public SlowMessageHandler(SlowHandlerCoordinator coordinator)
                => _coordinator = coordinator;

            public Task Handle(LockRenewalCommand message, IMessageHandlerContext context)
                => _coordinator.HoldDeliveryAsync();
        }

        // Edge-only raw-SDK read of the dedicated queue or its dead-letter sub-queue. PeekLock + explicit
        // Complete, so anything found is also removed and cannot leak into the sibling fact or a later run on
        // the shared emulator.
        private async Task<ServiceBusReceivedMessage> ReceiveAndCompleteAsync(SubQueue subQueue, TimeSpan timeout)
        {
            await using var client = new ServiceBusClient(_emulator.GetConnectionString());
            var receiver = client.CreateReceiver(
                LockRenewalQueue,
                new ServiceBusReceiverOptions
                {
                    SubQueue = subQueue,
                    ReceiveMode = ServiceBusReceiveMode.PeekLock,
                });

            var received = await receiver.ReceiveMessageAsync(timeout);
            if (received != null)
            {
                await receiver.CompleteMessageAsync(received);
            }

            return received;
        }

        // Clears the queue and its dead-letter sub-queue BEFORE a fact sends. The two facts share one dedicated
        // queue and the emulator outlives them both, so each fact starts from a known-empty entity regardless of
        // the order they ran in or of what a previous run left behind.
        private async Task PurgeQueueAsync()
        {
            await DrainAsync(SubQueue.None).ConfigureAwait(false);
            await DrainAsync(SubQueue.DeadLetter).ConfigureAwait(false);
        }

        private async Task DrainAsync(SubQueue subQueue)
        {
            for (var drained = 0; drained < MaxPurgeIterations; drained++)
            {
                var leftover = await ReceiveAndCompleteAsync(subQueue, PurgeReceiveWait).ConfigureAwait(false);
                if (leftover is null)
                {
                    return;
                }
            }
        }

        // THE PROOF. With renewal on (Chatter's default MaxMessageLockRenewalDuration of 5 minutes) a handler
        // that runs for 25 seconds against a 10-second lock keeps its lock: the handler is entered EXACTLY
        // once, the delivery is settled EXACTLY once — the queue is empty afterwards and the message never
        // comes back — and NOTHING reaches the dead-letter queue. Before the fix each of those three would have
        // failed: the handler would have been re-entered on redelivery, the completing settle would have hit a
        // lost lock, and the message would have been walked to MaxDeliveryCount and dead-lettered.
        [RequiresDockerFact]
        public async Task SlowHandlerKeepsItsLockAndSettlesExactlyOnceWithRenewalOn()
        {
            await PurgeQueueAsync();

            var coordinator = new SlowHandlerCoordinator(SlowHandlerDuration);

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<LockRenewalCommand>(
                    LockRenewalQueue,
                    transactionMode: TransactionMode.ReceiveOnly),
                services =>
                {
                    // Registered AFTER the harness's default RecordingMessageHandler<LockRenewalCommand>, so
                    // this slow handler is the one Chatter's dispatcher resolves on the receive path.
                    services.AddSingleton(coordinator);
                    services.AddTransient<IMessageHandler<LockRenewalCommand>, SlowMessageHandler>();
                },
                typeof(LockRenewalCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new LockRenewalCommand { Value = "slow-handler" }, LockRenewalQueue);
            }

            var handlerFinished = await coordinator.WaitForFirstCompletionAsync(HandlerCompletionWait);
            handlerFinished.Should().BeTrue(
                $"the handler holds the delivery for {SlowHandlerDuration} and must be allowed to run to completion " +
                $"within {HandlerCompletionWait}");

            // The handler ran EXACTLY once. The window is wider than the lock duration, so a delivery whose lock
            // had been lost would have been redelivered and re-entered inside it.
            var invocations = await coordinator.WaitForInvocationCountAsync(2, RedeliveryWindow);
            invocations.Should().Be(
                1,
                $"Chatter renews the message lock for the life of the delivery, so a handler running {SlowHandlerDuration} " +
                $"against a {QueueLockDuration} lock is never redelivered mid-flight; a second invocation is the issue " +
                "#373 defect");
            coordinator.CompletionCount.Should().Be(
                1,
                "exactly one handler invocation ran to completion");

            // The delivery was settled EXACTLY once, as a Complete: the queue is empty, so the completing settle
            // reached the broker, and the message has not returned. Read only after the redelivery window above,
            // so settlement has certainly resolved.
            var leftOnQueue = await ReceiveAndCompleteAsync(SubQueue.None, DrainWait);
            leftOnQueue.Should().BeNull(
                "the successfully handled delivery must have been completed exactly once and left the queue; a message " +
                "still there means the settle hit a lost lock and the broker released the delivery back");

            // NOTHING reached the dead-letter queue: the healthy message was never walked to MaxDeliveryCount.
            var deadLettered = await ReceiveAndCompleteAsync(SubQueue.DeadLetter, DrainWait);
            deadLettered.Should().BeNull(
                "a healthy, successfully handled message must never be dead-lettered; issue #373's signature is exactly " +
                "this message arriving on the dead-letter queue after repeated lock-loss redeliveries");
        }

        // THE CONTROL, and the reason the fact above is not vacuous. The SAME slow handler with renewal
        // explicitly disabled (MaxMessageLockRenewalDuration of zero — the documented "no renewal" setting)
        // reproduces issue #373 against this broker: the lock expires under the running handler, the message is
        // redelivered, and the entity's MaxDeliveryCount of 2 dead-letters a message that the handler was
        // handling perfectly well. If this ever stops dead-lettering, the broker has stopped expiring locks and
        // the positive fact above is proving nothing.
        [RequiresDockerFact]
        public async Task SlowHandlerLosesItsLockAndIsDeadLetteredWithRenewalDisabled()
        {
            await PurgeQueueAsync();

            var coordinator = new SlowHandlerCoordinator(SlowHandlerDuration);

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    sb.WithMaxMessageLockRenewalDuration(TimeSpan.Zero);
                    sb.AddQueueReceiver<LockRenewalCommand>(
                        LockRenewalQueue,
                        transactionMode: TransactionMode.ReceiveOnly);
                },
                services =>
                {
                    services.AddSingleton(coordinator);
                    services.AddTransient<IMessageHandler<LockRenewalCommand>, SlowMessageHandler>();
                },
                typeof(LockRenewalCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new LockRenewalCommand { Value = "slow-handler-no-renewal" }, LockRenewalQueue);
            }

            var deadLettered = await ReceiveAndCompleteAsync(SubQueue.DeadLetter, ControlDeadLetterWait);

            deadLettered.Should().NotBeNull(
                $"with renewal disabled the handler's {SlowHandlerDuration} of work outlives the {QueueLockDuration} " +
                $"lock, so the broker redelivers the message until its MaxDeliveryCount of 2 is exhausted and " +
                $"dead-letters it — that is issue #373. Nothing arriving within {ControlDeadLetterWait} means this " +
                "broker does not expire message locks, which would make the renewal-on fact vacuous");
            deadLettered.DeliveryCount.Should().BeGreaterThan(
                1,
                "each lost lock costs the healthy message another delivery attempt");
        }
    }
}
