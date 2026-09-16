using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.Receiving;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Startup POISON-DESTINATION EXISTENCE gate proved against a REAL broker (issue #366). This adapter provisions
    // no topology, so a dead-letter queue named in configuration but never declared externally does not exist —
    // and the deadletter's mandatory:true republish would fault, leaving the poison delivery unsettled and the
    // receiver stalled on its only prefetch credit. The gate must reject that at InitializeAsync, naming the queue.
    //
    // Driven at the SOURCE + RECEIVER level (not through ChatterRabbitMqPipelineHarness) so the gate's own contract
    // is observed directly, mirroring RabbitMqReceiverTeardownTests. Only the WORK queue is declared for the
    // rejection scenario; the clean-start scenario declares the dead-letter queue too.
    //
    // Gated by [RequiresDockerFact] + Category=Integration: SKIPPED when Docker is absent so a plain `dotnet test`
    // stays green; the nightly RabbitMQ CI lane runs it for real.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqStartupValidationTests
    {
        // Bounds the in-test topology declare so a wedged broker operation fails fast instead of hanging.
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqStartupValidationTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        [RequiresDockerFact]
        public async Task UndeclaredDeadLetterQueueMustRejectInitialization()
        {
            var amqpUri = _fixture.GetAmqpConnectionString();
            var set = RabbitMqTopology.CreateSet("startupvalidation_missing", QueueType.Quorum);

            // Declare ONLY the work queue: the configured dead-letter queue is deliberately left undeclared, which
            // is exactly the operator misconfiguration the gate exists to catch.
            await DeclareWorkQueueOnlyAsync(amqpUri, set, CancellationToken.None);
            var undeclaredDeadLetterQueue = $"{set.DeadLetterQueueName}_never_declared_{Guid.NewGuid():N}";

            var source = new RabbitMqConnectionSource(new RabbitMqOptions(uri: amqpUri, queueType: QueueType.Quorum));

            try
            {
                var receiver = NewReceiver(source, QueueType.Quorum);

                var initialize = () => receiver.InitializeAsync(
                    NewReceiverOptions(set.WorkQueueName, undeclaredDeadLetterQueue),
                    CancellationToken.None);

                (await initialize.Should().ThrowAsync<InvalidOperationException>(
                        "a dead-letter queue that was never declared on the broker must fail startup loudly, not stall the receiver on its first poison message"))
                    .Which.Message.Should().Contain(undeclaredDeadLetterQueue,
                        "the startup error must name the missing queue so the misconfiguration is actionable");
            }
            finally
            {
                await source.DisposeAsync();
            }
        }

        [RequiresDockerFact]
        public async Task DeclaredDeadLetterQueueMustStartCleanly()
        {
            var amqpUri = _fixture.GetAmqpConnectionString();
            var set = RabbitMqTopology.CreateSet("startupvalidation_declared", QueueType.Quorum);

            // Declares BOTH the work queue and the dead-letter queue, so the existence probe finds its target.
            await RabbitMqTopology.DeclareAsync(amqpUri, set, CancellationToken.None);

            var source = new RabbitMqConnectionSource(new RabbitMqOptions(uri: amqpUri, queueType: QueueType.Quorum));

            try
            {
                var receiver = NewReceiver(source, QueueType.Quorum);

                var initialize = () => receiver.InitializeAsync(
                    NewReceiverOptions(set.WorkQueueName, set.DeadLetterQueueName),
                    CancellationToken.None);

                await initialize.Should().NotThrowAsync(
                    "a dead-letter queue declared externally satisfies the existence gate, so the receiver starts consuming");

                await receiver.StopReceiver();
            }
            finally
            {
                await source.DisposeAsync();
            }
        }

        private static RabbitMqReceiver NewReceiver(RabbitMqConnectionSource source, QueueType queueType)
        {
            var bodyConverterFactory = new BodyConverterFactory(new IBrokeredMessageBodyConverter[]
            {
                new RabbitMqBodyConverter(),
                new JsonBodyConverter()
            });

            return new RabbitMqReceiver(source,
                                        new RabbitMqOptions(hostName: "unused", queueType: queueType),
                                        bodyConverterFactory,
                                        Mock.Of<ILogger<RabbitMqReceiver>>());
        }

        private static ReceiverOptions NewReceiverOptions(string receiverPath, string deadLetterQueuePath)
            => new ReceiverOptions
            {
                MessageReceiverPath = receiverPath,
                DeadLetterQueuePath = deadLetterQueuePath,
                MaxConcurrentCalls = 1
            };

        // Declares ONLY the work queue of the set (pinned to its queue type), deliberately leaving the set's
        // dead-letter queue undeclared — RabbitMqTopology.DeclareAsync always declares both, which is the exact
        // condition this scenario must NOT establish.
        private static async Task DeclareWorkQueueOnlyAsync(string amqpUri, RabbitMqTopology.RabbitMqObjectSet set, CancellationToken cancellationToken)
        {
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationCts.CancelAfter(OperationTimeout);
            var token = operationCts.Token;

            var factory = new ConnectionFactory { Uri = new Uri(amqpUri) };
            await using var connection = await factory.CreateConnectionAsync(token).ConfigureAwait(false);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: token).ConfigureAwait(false);

            await channel.QueueDeclareAsync(queue: set.WorkQueueName,
                                            durable: true,
                                            exclusive: false,
                                            autoDelete: false,
                                            arguments: new System.Collections.Generic.Dictionary<string, object>
                                            {
                                                ["x-queue-type"] = set.QueueType == QueueType.Quorum ? "quorum" : "classic"
                                            },
                                            cancellationToken: token).ConfigureAwait(false);
        }
    }
}
