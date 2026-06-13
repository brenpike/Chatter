using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using RabbitMQ.Client;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Topology declaration helper for the RabbitMQ integration harness. The production adapter PROVISIONS
    // NOTHING (docs/design/rabbitmq-adapter.md §10) — exchanges, queues, bindings, and the dead-letter / error
    // queues are assumed to already exist. So, mirroring how the SQL Service Broker harness provisions its
    // broker objects in-test (ServiceBrokerProvisioning), the integration tests declare exactly the queues /
    // exchanges they send/receive over against the container BEFORE running the pipeline, via the
    // RabbitMQ.Client management API.
    //
    // Each scenario gets its OWN immutable RabbitMqObjectSet (work queue + dead-letter queue, optionally an
    // explicit exchange + binding) so a poison message left by one scenario can never bleed into another's
    // work queue — the same per-scenario isolation the SSB ObjectSet provides. The work-queue QUEUE TYPE is
    // pinned per set via the x-queue-type declare argument, so the same scenario can be declared as a Quorum
    // queue and (separately) as a Classic queue to prove the delivery-count strategy on BOTH per ADR-0001.
    internal static class RabbitMqTopology
    {
        // Bounds the declare/teardown DB-equivalent awaits so a wedged broker operation fails fast.
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

        // An immutable per-scenario set of RabbitMQ objects. The work queue is what a receiver consumes; the
        // dead-letter queue is what the adapter's deadletter republish targets BY NAME (authoritative, not a
        // broker-side DLX). When Exchange is non-null an explicit direct exchange is declared and the work
        // queue bound to it under RoutingKey, so a WithRabbitMqRouting send reaches the work queue.
        public readonly record struct RabbitMqObjectSet(
            string WorkQueueName,
            string DeadLetterQueueName,
            QueueType QueueType,
            string Exchange = null,
            string RoutingKey = null)
        {
            public bool HasExchange => !string.IsNullOrWhiteSpace(Exchange);
        }

        // Mints a per-scenario object set. The queue-type suffix keeps the Quorum and Classic variants of the
        // same scenario on distinct queues so they never share state.
        public static RabbitMqObjectSet CreateSet(string suffix, QueueType queueType, string exchange = null, string routingKey = null)
        {
            var typeSuffix = queueType == QueueType.Quorum ? "quorum" : "classic";
            return new RabbitMqObjectSet(
                WorkQueueName: $"chatter_rmq_it_work_{suffix}_{typeSuffix}",
                DeadLetterQueueName: $"chatter_rmq_it_deadletter_{suffix}_{typeSuffix}",
                QueueType: queueType,
                Exchange: exchange,
                RoutingKey: routingKey);
        }

        // Declares the work queue (pinned to the set's queue type via x-queue-type), the dead-letter queue (as a
        // classic queue — it only ever receives republished envelopes, never counts deliveries), and, when the
        // set carries an exchange, the exchange + binding. Idempotent: AMQP declares are no-ops when the object
        // already exists with matching arguments, so re-runs never throw.
        public static async Task DeclareAsync(string amqpUri, RabbitMqObjectSet set, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(amqpUri))
            {
                throw new ArgumentException("An AMQP connection URI is required.", nameof(amqpUri));
            }

            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationCts.CancelAfter(OperationTimeout);
            var token = operationCts.Token;

            var factory = new ConnectionFactory { Uri = new Uri(amqpUri) };
            await using var connection = await factory.CreateConnectionAsync(token).ConfigureAwait(false);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: token).ConfigureAwait(false);

            var workQueueArgs = new Dictionary<string, object>
            {
                ["x-queue-type"] = set.QueueType == QueueType.Quorum ? "quorum" : "classic",
            };

            await channel.QueueDeclareAsync(queue: set.WorkQueueName,
                                            durable: true,
                                            exclusive: false,
                                            autoDelete: false,
                                            arguments: workQueueArgs,
                                            cancellationToken: token).ConfigureAwait(false);

            // The dead-letter queue is a plain durable classic queue: it is the republish TARGET, so it never
            // needs a queue-type-specific delivery counter of its own.
            await channel.QueueDeclareAsync(queue: set.DeadLetterQueueName,
                                            durable: true,
                                            exclusive: false,
                                            autoDelete: false,
                                            arguments: null,
                                            cancellationToken: token).ConfigureAwait(false);

            if (set.HasExchange)
            {
                await channel.ExchangeDeclareAsync(exchange: set.Exchange,
                                                   type: ExchangeType.Direct,
                                                   durable: true,
                                                   autoDelete: false,
                                                   arguments: null,
                                                   cancellationToken: token).ConfigureAwait(false);

                await channel.QueueBindAsync(queue: set.WorkQueueName,
                                             exchange: set.Exchange,
                                             routingKey: set.RoutingKey ?? set.WorkQueueName,
                                             arguments: null,
                                             cancellationToken: token).ConfigureAwait(false);
            }
        }
    }
}
