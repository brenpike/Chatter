using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // The dead-letter envelope the RabbitMQ deadletter republish wrote, read at the test edge via raw
    // RabbitMQ.Client (BasicGet). The adapter republishes the ORIGINAL message body (the command JSON) to the
    // dead-letter queue by name, merging the inbound AMQP headers with the FailureDetails / FailureDescription
    // overrides DeadletterMessageAsync stamps — so the failure metadata lives in the AMQP message HEADERS, not
    // the body. Headers is exposed as string values decoded tolerantly (AMQP string headers arrive as byte[]).
    internal sealed class DeadLetterEnvelope
    {
        public DeadLetterEnvelope(byte[] body, IReadOnlyDictionary<string, string> headers)
        {
            Body = body;
            Headers = headers;
        }

        public byte[] Body { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }

        public string BodyAsString => Body is null ? null : Encoding.UTF8.GetString(Body);
    }

    // Bounded poll of a dead-letter queue, returning the first republished envelope or null if the deadline
    // elapses with no message (so the caller's assertion fails fast rather than hanging). The deadletter
    // republish happens AFTER the handler throw on the receiver thread, so the poll absorbs the race between the
    // republish-then-ack and the test read.
    internal static class DeadLetterQueueReader
    {
        public static async Task<DeadLetterEnvelope> ReceiveAsync(string amqpUri, string deadLetterQueueName, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(amqpUri))
            {
                throw new ArgumentException("An AMQP connection URI is required.", nameof(amqpUri));
            }

            using var operationCts = new CancellationTokenSource(timeout);
            var token = operationCts.Token;

            var factory = new ConnectionFactory { Uri = new Uri(amqpUri) };
            await using var connection = await factory.CreateConnectionAsync(token).ConfigureAwait(false);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: token).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var result = await channel.BasicGetAsync(deadLetterQueueName, autoAck: true, cancellationToken: token)
                    .ConfigureAwait(false);
                if (result is not null)
                {
                    return new DeadLetterEnvelope(result.Body.ToArray(), DecodeHeaders(result.BasicProperties?.Headers));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }

            return null;
        }

        // AMQP string headers arrive boxed as byte[]; numeric headers as boxed int/long. Decode every header to a
        // string so the test asserts on header presence + value without re-implementing the boxing rules.
        private static IReadOnlyDictionary<string, string> DecodeHeaders(IDictionary<string, object> headers)
        {
            var decoded = new Dictionary<string, string>();
            if (headers is null)
            {
                return decoded;
            }

            foreach (var entry in headers)
            {
                decoded[entry.Key] = entry.Value switch
                {
                    null => null,
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    _ => entry.Value.ToString(),
                };
            }

            return decoded;
        }
    }
}
