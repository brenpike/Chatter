using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // END-TO-END (criterion 5) entirely through Chatter public API: a participant command delivered via
    // IReceivedMessageDispatcher -> the framework opens+commits the document-tier batch (aggregate + co-resident outbox)
    // -> the REAL relay drains the committed outbox doc -> the capturing broker sink receives the handler's outbound
    // message. The assertion is a bounded wait on the captured publication matching the handler's follow-up.
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenRunningEndToEnd
    {
        private const string ReceiverPath = "primary-participant";
        private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(120);

        private readonly CosmosEmulatorFixture _emulator;

        public WhenRunningEndToEnd(CosmosEmulatorFixture emulator) => _emulator = emulator;

        [RequiresDockerFact]
        public async Task HandlerThroughFrameworkBatchThroughRelayReachesBrokerSink()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);

            string partition = "pk-" + Guid.NewGuid().ToString("N");
            string aggregateId = "agg-" + Guid.NewGuid().ToString("N");
            string messageId = "msg-" + Guid.NewGuid().ToString("N");
            // A UNIQUE destination isolates this test's publication from other tests' docs the shared relay also drains.
            string destination = "e2e-dest-" + Guid.NewGuid().ToString("N");

            await using CosmosReliabilityHarness harness = CosmosReliabilityHarness.Build(
                testClient.Client,
                pipeline => pipeline.WithCosmosDocumentReliability<PrimaryParticipantCommand>(
                    CosmosTestClient.DatabaseName,
                    CosmosTestClient.DocumentContainerName,
                    CosmosTestClient.LeaseContainerName,
                    TestResolvers.ResolvePartition,
                    CosmosTestClient.PartitionKeyPath),
                services => services.AddTransient<IMessageHandler<PrimaryParticipantCommand>, PrimaryParticipantHandler>());

            // Start the relay BEFORE delivery so the change-feed processor is already draining when the outbox commits
            // (the .WithStartTime backlog drain also covers the case where delivery races ahead of start).
            await harness.StartAsync();

            await harness.DeliverAsync(
                messageId,
                new PrimaryParticipantCommand { AggregateId = aggregateId, Partition = partition, Payload = "e2e", OutboundDestination = destination, OutboundMessageId = "out-" + Guid.NewGuid().ToString("N") },
                ReceiverPath,
                new Dictionary<string, object> { [TestResolvers.PartitionProperty] = partition });

            // The relayed message is the handler's follow-up: it reaches the broker sink at the handler's unique
            // destination. (The follow-up's MessageId is Chatter-generated — SendOptions.MessageId does not survive the
            // core handler-context Send merge — so the match is on the unique destination the handler addressed.)
            await WaitForPublishedToDestinationAsync(harness.Capture, destination, PublishTimeout);
            harness.Capture.Published.Count(m => m.Destination == destination)
                .Should().Be(1, "the handler's outbound, committed on the framework batch, must reach the broker sink via the relay");
        }

        private static async Task WaitForPublishedToDestinationAsync(CapturingInfrastructure capture, string destination, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            do
            {
                if (capture.Published.Any(m => m.Destination == destination))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            while (DateTime.UtcNow < deadline);

            throw new Xunit.Sdk.XunitException($"No publication to destination '{destination}' was captured within {timeout}.");
        }
    }
}
