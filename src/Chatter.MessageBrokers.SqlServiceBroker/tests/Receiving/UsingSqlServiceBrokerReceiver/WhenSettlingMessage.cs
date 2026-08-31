using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingSqlServiceBrokerReceiver
{
    // Contract test for SqlServiceBrokerReceiver's settlement outcomes against the three-valued
    // IMessagingInfrastructureReceiver settlement contract (Settled / NotRequired / Failed).
    //
    // Observation seam: the settlement methods are driven directly with a MessageBrokerContext and a
    // TransactionContext. Rationale: every row below is a path that issues NO SQL — the Dialog Commands
    // (END CONVERSATION) and the deadletter dispatch all sit behind the ReceivedMessage guard, and
    // ReceiveSession's commit/rollback are null-transaction-safe — so the classification is observable
    // without a live Service Broker. The SQL-dependent Settled rows stay covered by tests/Integration.
    //
    // An EMPTY TransactionContext container is exactly what TransactionMode.None produces:
    // ReceiveMessageAsync includes the SqlTransaction in the container only when the mode is not None,
    // so the absent transaction — not the configured mode — is the observable discriminator.
    public class WhenSettlingMessage : Testing.Core.Context
    {
        private static SqlServiceBrokerOptions SsbOptions()
            => new SqlServiceBrokerOptions(
                connectionString: "Server=(local);Database=test;Integrated Security=true;",
                messageBodyType: "application/json; charset=utf-16");

        private static async Task<SqlServiceBrokerReceiver> CreateInitializedReceiver(TransactionMode transactionMode)
        {
            var receiver = new SqlServiceBrokerReceiver(
                SsbOptions(),
                new InMemorySqlConnectionSource(),
                new MessageBrokerOptions { TransactionMode = transactionMode },
                Mock.Of<ILogger<SqlServiceBrokerReceiver>>(),
                Mock.Of<IBodyConverterFactory>(),
                Mock.Of<IServiceScopeFactory>());

            await receiver.InitializeAsync(
                new ReceiverOptions
                {
                    MessageReceiverPath = "test-queue",
                    DeadLetterQueuePath = "test-deadletter-queue",
                    TransactionMode = transactionMode
                },
                CancellationToken.None);

            return receiver;
        }

        private static MessageBrokerContext ContextWithoutReceivedMessage()
            => new MessageBrokerContext(
                messageId: Guid.NewGuid().ToString(),
                body: Array.Empty<byte>(),
                applicationProperties: new Dictionary<string, object>(),
                messageReceiverPath: "test-queue",
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: new JsonUnicodeBodyConverter());

        [Fact]
        public async Task MustReportNotRequiredWhenNegativelyAcknowledgingWithoutATransaction()
        {
            var receiver = await CreateInitializedReceiver(TransactionMode.None);

            var result = await receiver.NackMessageAsync(
                ContextWithoutReceivedMessage(),
                new TransactionContext("test-queue", TransactionMode.None),
                CancellationToken.None);

            result.Outcome.Should().Be(SettlementOutcome.NotRequired,
                "TransactionMode.None leaves no transaction to roll back, so nothing is returned for " +
                "redelivery and no negative acknowledgment is owed");
            result.IsSettled.Should().BeFalse();
            result.Reason.Should().NotBeNullOrWhiteSpace(
                "an unsettled outcome must explain itself");
        }

        [Fact]
        public async Task MustReportNotRequiredWhenAcknowledgingWithoutAReceivedMessage()
        {
            var receiver = await CreateInitializedReceiver(TransactionMode.None);

            var result = await receiver.AckMessageAsync(
                ContextWithoutReceivedMessage(),
                new TransactionContext("test-queue", TransactionMode.None),
                CancellationToken.None);

            result.Outcome.Should().Be(SettlementOutcome.NotRequired,
                "with no ReceivedMessage there is no Conversation to end, and TransactionMode.None " +
                "leaves no transaction to commit, so there is nothing to acknowledge");
            result.IsSettled.Should().BeFalse();
            result.Reason.Should().NotBeNullOrWhiteSpace(
                "an unsettled outcome must explain itself");
        }

        [Fact]
        public async Task MustReportFailedInsteadOfThrowingWhenDeadletteringWithoutAReceivedMessage()
        {
            var receiver = await CreateInitializedReceiver(TransactionMode.ReceiveOnly);

            var result = await receiver.DeadletterMessageAsync(
                ContextWithoutReceivedMessage(),
                new TransactionContext("test-queue", TransactionMode.ReceiveOnly),
                "deadletter reason",
                "deadletter error description",
                CancellationToken.None);

            result.Outcome.Should().Be(SettlementOutcome.Failed,
                "a missing ReceivedMessage is a DETERMINISTIC fault that retrying could never fix, so it " +
                "is reported as a terminal Failed rather than thrown into Recovery's retry path");
            result.IsSettled.Should().BeFalse();
            result.Reason.Should().NotBeNullOrWhiteSpace(
                "an unsettled outcome must explain itself");
        }

        [Fact]
        public async Task MustReportFailedWhenDeadletteringANullContext()
        {
            var receiver = await CreateInitializedReceiver(TransactionMode.ReceiveOnly);

            var result = await receiver.DeadletterMessageAsync(
                null,
                new TransactionContext("test-queue", TransactionMode.ReceiveOnly),
                "deadletter reason",
                "deadletter error description",
                CancellationToken.None);

            result.Outcome.Should().Be(SettlementOutcome.Failed,
                "an absent context carries no ReceivedMessage either, and is the same deterministic fault");
            result.IsSettled.Should().BeFalse();
        }
    }
}
