using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Missing-queue fail-fast proof for SqlServiceBrokerReceiver (#358).
    //
    // This package provisions no Service Broker topology, so a receiver pointed at a queue that does not
    // exist is deterministic misconfiguration: SQL Server fails the RECEIVE batch at compile time with error
    // 208 ("Invalid object name"), and no amount of retrying can make the queue appear. The receiver must
    // therefore surface a CriticalReceiverException naming the configured queue rather than letting the raw
    // SqlException escape into the core recovery pipeline.
    //
    // The queue named below is DELIBERATELY absent from ServiceBrokerProvisioning — the missing object IS the
    // fixture for this fact. The receiver is constructed directly (internal, via InternalsVisibleTo) rather
    // than through ChatterSsbPipelineHarness because the fault must be observed at the receive seam, before
    // the pump's own error handling folds it into a background loop.
    //
    // Gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green. Mirrors SsbDialogLifecycleTests for collection membership.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbMissingQueueTests
    {
        // Never provisioned by ServiceBrokerProvisioning. Do NOT add it there — its absence is the fixture.
        private const string MissingQueueName = "chatter_ssb_it_missing_queue";
        private const string MissingQueuePathBracketed = "[" + MissingQueueName + "]";

        // Bounds the receive so a wedged connection cannot hang the collection. SQL Server rejects the batch
        // at compile time, so the real path returns in milliseconds; this is only the hang guard.
        private static readonly TimeSpan ReceiveBound = TimeSpan.FromSeconds(30);

        // Finite WAITFOR timeout so that even a hypothetical successful compile could not wait forever.
        private const int ReceiverTimeoutInMilliseconds = 5000;

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbMissingQueueTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        private SqlServiceBrokerReceiver CreateReceiver()
        {
            var ssbOptions = new SqlServiceBrokerOptions(
                connectionString: _fixture.GetAppConnectionString(),
                messageBodyType: "application/json; charset=utf-16",
                receiverTimeoutInMilliseconds: ReceiverTimeoutInMilliseconds);

            return new SqlServiceBrokerReceiver(
                ssbOptions,
                new SqlClientConnectionSource(ssbOptions),
                new MessageBrokerOptions { TransactionMode = TransactionMode.ReceiveOnly },
                Mock.Of<ILogger<SqlServiceBrokerReceiver>>(),
                Mock.Of<IBodyConverterFactory>(),
                Mock.Of<IServiceScopeFactory>());
        }

        // A RECEIVE against an unprovisioned queue raises SQL error 208. The receiver must translate it into a
        // CriticalReceiverException whose message names the configured MessageReceiverPath, so the operator
        // reads which queue is missing instead of a bare "Invalid object name".
        [RequiresDockerFact]
        public async Task ReceivingFromAMissingQueueSurfacesACriticalReceiverExceptionNamingTheQueue()
        {
            var receiver = CreateReceiver();
            try
            {
                await receiver.InitializeAsync(
                    new ReceiverOptions { MessageReceiverPath = MissingQueuePathBracketed },
                    CancellationToken.None);

                using var receiveCts = new CancellationTokenSource(ReceiveBound);

                Func<Task> receiving = () => receiver.ReceiveMessageAsync(
                    new TransactionContext(MissingQueuePathBracketed, TransactionMode.ReceiveOnly),
                    receiveCts.Token);

                (await receiving.Should().ThrowAsync<CriticalReceiverException>(
                        "a queue that was never provisioned is deterministic misconfiguration, so the receive " +
                        "must fail fast as critical rather than escaping as a retryable SqlException"))
                    .WithMessage($"*{MissingQueueName}*",
                        "the operator must be told WHICH configured queue is missing");
            }
            finally
            {
                await receiver.DisposeAsync();
            }
        }
    }
}
