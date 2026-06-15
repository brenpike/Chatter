using System;
using System.Linq;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Transaction-mode integration proof (C9) for the SQL Service Broker integration harness. The SYSTEM UNDER
    // TEST is how the GLOBAL MessageBrokerOptions.TransactionMode governs the SSB receive-side RECEIVE
    // transaction, which is what makes nack→redelivery possible (or not):
    //
    //   * ReceiveOnly — SqlServiceBrokerReceiver BEGINs a transaction around the RECEIVE, so when the handler
    //     throws, NackMessageAsync ROLLS BACK that transaction and the message returns to the queue and is
    //     REDELIVERED (InvocationCount climbs, ReceiveAttempts climbs). This is the same redelivery SsbNack
    //     RedeliveryTests proves; here it is the POSITIVE control for the transaction-mode lever.
    //   * None — the receiver BEGINs NO transaction around the RECEIVE (SqlServiceBrokerReceiver only opens a
    //     transaction when _transactionMode != TransactionMode.None), so the RECEIVE is already committed when
    //     the handler throws — there is nothing to roll back, so the message is NOT returned to the queue and is
    //     NOT redelivered. The handler is invoked EXACTLY ONCE.
    //
    // WHY TWO HARNESS INSTANCES: the SSB receive-side transaction mode is GLOBAL-PER-CONTAINER. SqlService
    // BrokerReceiver captures it ONCE in its ctor from MessageBrokerOptions.TransactionMode
    // (SqlServiceBrokerReceiver.cs: `_transactionMode = messageBrokerOptions?.TransactionMode ??
    // TransactionMode.ReceiveOnly`), NOT per-receiver — the AddQueueReceiver(transactionMode:) param does not
    // reach the SSB receive-side SQL transaction. So the only lever is the GLOBAL MessageBrokerOptions
    // .TransactionMode set via AddMessageBrokers(opts => opts.WithTransactionMode(...)). Each mode therefore
    // needs its OWN harness instance (its own DI graph / receiver) built with that global mode, and each runs on
    // its OWN provisioned object set so their queue state can never bleed into one another.
    //
    // Both facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green. Mirrors SsbNackRedeliveryTests for harness setup and collection membership.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbTransactionModeTests
    {
        private static readonly TimeSpan RedeliveryWait = TimeSpan.FromSeconds(30);

        // For the None case: how long to keep watching after the single delivery is confirmed to PROVE no
        // redelivery ever invokes the handler a second time. With no receive transaction to roll back, a (wrong)
        // redelivery would have to happen within this window; a bounded settle confirms the negative is not
        // merely un-raced.
        private static readonly TimeSpan NoRedeliverySettle = TimeSpan.FromSeconds(5);

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbTransactionModeTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        // Distinct command type for the ReceiveOnly case so its queue state is independent of every other
        // integration test class in the collection.
        public sealed class ReceiveOnlyTransactionCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // Distinct command type for the None case, on a DIFFERENT object set, so the two transaction-mode cases
        // never share a queue.
        public sealed class NoneTransactionCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // ReceiveOnly harness: built on the dedicated TransactionSet with the GLOBAL transaction mode set to
        // ReceiveOnly so the receiver BEGINs a transaction around the RECEIVE and a throwing handler's nack rolls
        // it back to redeliver.
        private ChatterSsbPipelineHarness BuildReceiveOnlyHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                ServiceBrokerProvisioning.TransactionSet,
                ssb => ssb.AddQueueReceiver<ReceiveOnlyTransactionCommand>(
                    ServiceBrokerProvisioning.TransactionSet.TargetQueuePathBracketed,
                    deadLetterServicePath: ServiceBrokerProvisioning.TransactionSet.DeadLetterServiceName),
                globalTransactionMode: TransactionMode.ReceiveOnly,
                typeof(ReceiveOnlyTransactionCommand));

        // None harness: built on its OWN dedicated object set (NoneSet) with the GLOBAL transaction mode set to
        // None so the receiver BEGINs NO transaction around the RECEIVE — a throwing handler cannot redeliver
        // because there is no receive transaction to roll back. A dedicated set (not the shared DeadLetterSet)
        // is required: the collection fixture provisions once and never clears queues between test classes, so
        // reusing DeadLetterSet's target/deadletter queues would let leftover state or class-order interaction
        // from SsbDeadLetterTests bleed into this receiver and make the exactly-once None assertion
        // order-dependent. Its own set keeps C9-None isolated, exactly as Publish/Poison/Transaction/Forwarding.
        private ChatterSsbPipelineHarness BuildNoneHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                ServiceBrokerProvisioning.NoneSet,
                ssb => ssb.AddQueueReceiver<NoneTransactionCommand>(
                    ServiceBrokerProvisioning.NoneSet.TargetQueuePathBracketed,
                    deadLetterServicePath: ServiceBrokerProvisioning.NoneSet.DeadLetterServiceName),
                globalTransactionMode: TransactionMode.None,
                typeof(NoneTransactionCommand));

        // Global ReceiveOnly + throwing handler → redelivery. The receiver wraps the RECEIVE in a transaction, so
        // NackMessageAsync rolls it back and the message returns to the queue. Assert (a) the handler is invoked
        // at least twice and (b) ReceiveAttempts climbs above 1 across deliveries. ANTI-INFINITE-LOOP: stop
        // throwing once >= 2 invocations are observed so the message finally acks and the conversation closes
        // before DisposeAsync drains the pump.
        [RequiresDockerFact]
        public async Task GlobalReceiveOnlyModeRedeliversThrowingHandler()
        {
            var harness = BuildReceiveOnlyHarness();
            try
            {
                await harness.StartAsync();

                // Arm the throw BEFORE sending so the handler throws on the very first delivery.
                harness.GetSignal<ReceiveOnlyTransactionCommand>().ThrowOnHandle =
                    () => new InvalidOperationException("receive-only-transaction-mode forced throw");

                await harness.SendAsync(new ReceiveOnlyTransactionCommand { Marker = "receive-only" });

                // Wait until at least 2 handler invocations have been observed (first delivery + at least one
                // redelivery). WaitForInvocationCountAsync returns the last observed count, which may be below
                // minCount when the timeout elapses — the assertion below catches that case explicitly.
                var observedCount = await harness.WaitForInvocationCountAsync<ReceiveOnlyTransactionCommand>(
                    minCount: 2, RedeliveryWait);

                // ANTI-INFINITE-LOOP: stop throwing so the next receive acks and the conversation closes cleanly.
                harness.GetSignal<ReceiveOnlyTransactionCommand>().ThrowOnHandle = null;

                observedCount.Should().BeGreaterThanOrEqualTo(2,
                    "under global ReceiveOnly mode the receiver wraps the RECEIVE in a transaction, so a throwing " +
                    "handler's nack rolls it back and the message is redelivered — the handler must be invoked at " +
                    "least twice");

                // ReceiveAttempts must climb: the receiver's in-memory attempt counter increments on each
                // re-receive of the same conversation handle.
                var records = harness.GetSignal<ReceiveOnlyTransactionCommand>().Records.ToList();
                var maxAttempts = records
                    .Where(r => r.Context?.BrokeredMessage?.MessageContext?.ContainsKey(MessageContext.ReceiveAttempts) == true)
                    .Select(r => Convert.ToInt32(r.Context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts]))
                    .DefaultIfEmpty(0)
                    .Max();

                maxAttempts.Should().BeGreaterThan(1,
                    "ReceiveAttempts must climb above 1 once the rolled-back RECEIVE redelivers the same " +
                    "conversation handle at least once");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // Global None + throwing handler → exactly one invocation, NO redelivery. The receiver opens NO
        // transaction around the RECEIVE, so the RECEIVE is already committed when the handler throws — there is
        // nothing to roll back and the message is not returned to the queue. First CONFIRM the single delivery
        // landed (non-vacuous: the handler really was invoked once), then assert it stays at exactly 1 across a
        // bounded settle so the no-redelivery negative is observed, not merely un-raced.
        [RequiresDockerFact]
        public async Task GlobalNoneModeDoesNotRedeliverThrowingHandler()
        {
            var harness = BuildNoneHarness();
            try
            {
                await harness.StartAsync();

                // Arm the throw BEFORE sending so the handler throws on the first (and only) delivery.
                harness.GetSignal<NoneTransactionCommand>().ThrowOnHandle =
                    () => new InvalidOperationException("none-transaction-mode forced throw");

                await harness.SendAsync(new NoneTransactionCommand { Marker = "none" });

                // NON-VACUOUS: first confirm the single delivery actually landed (handler invoked at least once)
                // so the no-redelivery assertion below is not trivially true on a message that never arrived.
                var firstObservedCount = await harness.WaitForInvocationCountAsync<NoneTransactionCommand>(
                    minCount: 1, RedeliveryWait);

                firstObservedCount.Should().BeGreaterThanOrEqualTo(1,
                    "the message must be delivered at least once under None mode before asserting it is not " +
                    "redelivered — otherwise the no-redelivery assertion would be vacuous");

                // NO REDELIVERY: with no receive transaction to roll back, the throwing handler cannot return the
                // message to the queue. Let the pump loop for a bounded settle so a (wrongly) redelivered message
                // would have time to invoke the handler again, then assert the count stayed at exactly 1.
                await Task.Delay(NoRedeliverySettle).ConfigureAwait(false);

                harness.GetSignal<NoneTransactionCommand>().InvocationCount
                    .Should().Be(1,
                        "under global None mode the receiver opens no transaction around the RECEIVE, so a " +
                        "throwing handler has nothing to roll back — the message is not redelivered and the " +
                        "handler is invoked exactly once even after a bounded settle window");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
