using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes the relay host's change-feed STREAM handler error bias. Normal handler completion is the SDK's
    /// checkpoint signal, so a batch payload the relay cannot parse must FAIL CLOSED (throw -> no checkpoint) rather than
    /// return normally and let the lease advance past potentially-unpublished outbox documents (at-least-once).
    /// </summary>
    public class WhenHandlingChangeFeedBatch
    {
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private static CosmosOutboxRelayHostedService Host()
        {
            var registry = new DocumentReliabilityRegistry();
            var containerFactory = new CosmosContainerFactory(Mock.Of<IServiceProvider>());
            var provider = Mock.Of<IMessagingInfrastructureProvider>();
            var bodyConverterFactory = Mock.Of<IBodyConverterFactory>();
            return new CosmosOutboxRelayHostedService(registry, containerFactory, provider, bodyConverterFactory);
        }

        private static Stream StreamOf(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

        // The gate ONE processor drains through, standing in for the one BuildChangeFeedHandler constructs per
        // descriptor. No host owns a gate any more, so the handler is handed one.
        private static OutboxDrainGate ProcessorGate() => new OutboxDrainGate(new GuardedRelayLog(logger: null));

        [Theory]
        [InlineData("{}")]                                  // no Documents property at all
        [InlineData("{\"Documents\":\"not-an-array\"}")]   // Documents present but not an array
        [InlineData("{\"Documents\":{}}")]                  // Documents present but an object
        [InlineData("{\"documents\":[]}")]                  // wrong-cased property (Documents absent)
        public async Task MustFailClosedOnAMalformedBatchPayload(string payload)
        {
            CosmosOutboxRelayHostedService host = Host();

            Func<Task> act = () => host.HandleChangesAsync(StreamOf(payload), Mock.Of<Container>(), PartitionKeyPath, "lease-0", ProcessorGate(), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>(
                "a batch whose 'Documents' array cannot be read must fault so the SDK does not checkpoint the lease past unpublished documents");
        }

        [Fact]
        public async Task MustNotThrowOnAWellFormedEmptyBatch()
        {
            // A well-formed batch that simply contains no documents is a normal no-op (checkpoint is correct — there was
            // nothing to publish). Only an UNPARSEABLE batch shape fails closed.
            CosmosOutboxRelayHostedService host = Host();

            Func<Task> act = () => host.HandleChangesAsync(StreamOf("{\"Documents\":[]}"), Mock.Of<Container>(), PartitionKeyPath, "lease-0", ProcessorGate(), CancellationToken.None);

            await act.Should().NotThrowAsync("an empty but well-formed Documents array is a legitimate no-op batch");
        }
    }
}
