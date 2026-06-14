using System;
using System.Linq;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Nack→redelivery integration proof for the SQL Service Broker integration harness (STEP-006). The
    // SYSTEM UNDER TEST is Chatter's nack path: when RecordingMessageHandler<T> throws, SqlServiceBrokerReceiver's
    // NackMessageAsync rolls back the RECEIVE transaction so the message returns to the queue and is redelivered.
    // The test asserts (a) the handler is invoked at least twice (proving redelivery happened), and (b) the
    // ReceiveAttempts stamp in the MessageContext climbs across deliveries (proving the in-memory
    // ConcurrentDictionary<Guid,int> attempt counter increments on each re-receive). Mirrors SsbRoundTripTests
    // for harness setup and collection membership.
    //
    // ANTI-INFINITE-LOOP: ThrowOnHandle is flipped to null as soon as >= 2 invocations are observed so the
    // message finally acks and the conversation closes cleanly before DisposeAsync runs.
    //
    // The fact is gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbNackRedeliveryTests
    {
        private static readonly TimeSpan RedeliveryWait = TimeSpan.FromSeconds(30);

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbNackRedeliveryTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        // Distinct command type so this test's queue state is independent of SsbRoundTripTests.
        public sealed class NackRedeliveryCommand : ICommand
        {
            public string Marker { get; set; }
        }

        private ChatterSsbPipelineHarness BuildHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                ServiceBrokerProvisioning.NackSet,
                ssb => ssb.AddQueueReceiver<NackRedeliveryCommand>(
                    ServiceBrokerProvisioning.NackSet.TargetQueuePathBracketed,
                    deadLetterServicePath: ServiceBrokerProvisioning.NackSet.DeadLetterServiceName),
                typeof(NackRedeliveryCommand));

        // Nack→redelivery: when the handler throws, NackMessageAsync rolls back the RECEIVE transaction so the
        // message returns to the queue and is redelivered. Assert invocation count >= 2 (at least one redelivery)
        // and that ReceiveAttempts climbs across successive deliveries (the receiver's in-memory attempt counter
        // increments on each re-receive via ConcurrentDictionary<Guid,int>.AddOrUpdate).
        [RequiresDockerFact]
        public async Task ThrowingHandlerCausesRedeliveryAndClimbingReceiveAttempts()
        {
            var harness = BuildHarness();
            try
            {
                await harness.StartAsync();

                // Arm the throw BEFORE sending so the handler throws on the very first delivery.
                // ThrowOnHandle is Func<Exception>: returning a fresh instance per invocation matches the
                // contract RecordingMessageHandler<T> uses (it calls thrower() and throws the result).
                harness.GetSignal<NackRedeliveryCommand>().ThrowOnHandle =
                    () => new InvalidOperationException("nack-redelivery-test forced throw");

                await harness.SendAsync(new NackRedeliveryCommand { Marker = "nack-redelivery" });

                // Wait until at least 2 handler invocations have been observed (first delivery + at least one
                // redelivery). WaitForInvocationCountAsync returns the last observed count, which may be below
                // minCount when the timeout elapses — the assertion below catches that case explicitly.
                var observedCount = await harness.WaitForInvocationCountAsync<NackRedeliveryCommand>(
                    minCount: 2, RedeliveryWait);

                // ANTI-INFINITE-LOOP: stop throwing so the message is acked on the next receive and the
                // conversation closes cleanly before DisposeAsync drains the pump.
                harness.GetSignal<NackRedeliveryCommand>().ThrowOnHandle = null;

                observedCount.Should().BeGreaterThanOrEqualTo(2,
                    "the handler must be invoked at least twice: once for the original delivery and once for " +
                    "the redelivery after NackMessageAsync rolled back the RECEIVE transaction");

                // ReceiveAttempts must climb: the receiver's ConcurrentDictionary<Guid,int> increments the
                // attempt count on each re-receive of the same conversation handle. Capture the attempt stamp
                // from each recorded invocation and assert the maximum observed value exceeds 1.
                var records = harness.GetSignal<NackRedeliveryCommand>().Records.ToList();
                var maxAttempts = records
                    .Where(r => r.Context?.BrokeredMessage?.MessageContext?.ContainsKey(MessageContext.ReceiveAttempts) == true)
                    .Select(r => Convert.ToInt32(r.Context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts]))
                    .DefaultIfEmpty(0)
                    .Max();

                maxAttempts.Should().BeGreaterThan(1,
                    "the SSB receiver's in-memory attempt counter must increment on each re-receive of the same " +
                    "conversation handle, so ReceiveAttempts must exceed 1 after at least one redelivery");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
