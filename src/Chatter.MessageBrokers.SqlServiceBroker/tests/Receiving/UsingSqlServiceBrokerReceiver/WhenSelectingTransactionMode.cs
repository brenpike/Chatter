using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingSqlServiceBrokerReceiver
{
    // Pins the per-receiver TransactionMode selection contract introduced in STEP-001 (#235):
    // SqlServiceBrokerReceiver.InitializeAsync applies options.TransactionMode when non-null,
    // falling back to the ctor-captured global otherwise.
    //
    // Observation seam: reflection on the private _transactionMode field after InitializeAsync.
    // Rationale: InitializeAsync only sets the field and returns Task.CompletedTask — no SQL
    // connection is touched — so the field value is directly observable without driving
    // ReceiveMessageAsync (which requires a live SqlConnection for BeginTransactionAsync).
    // No production code changes are required; InternalsVisibleTo already grants the test
    // assembly access to the internal SqlServiceBrokerReceiver type.
    public class WhenSelectingTransactionMode : Testing.Core.Context
    {
        // INVARIANT: the private field name must match the production source exactly; a rename
        // breaks this test loudly rather than silently misreporting the selected mode.
        private const string TransactionModeFieldName = "_transactionMode";

        private static SqlServiceBrokerOptions SsbOptions()
            => new SqlServiceBrokerOptions(
                connectionString: "Server=(local);Database=test;Integrated Security=true;",
                messageBodyType: "application/json; charset=utf-16");

        private static SqlServiceBrokerReceiver CreateReceiver(TransactionMode? globalMode)
        {
            MessageBrokerOptions brokerOptions = globalMode.HasValue
                ? new MessageBrokerOptions { TransactionMode = globalMode.Value }
                : null;

            return new SqlServiceBrokerReceiver(
                SsbOptions(),
                new InMemorySqlConnectionSource(),
                brokerOptions,
                Mock.Of<ILogger<SqlServiceBrokerReceiver>>(),
                Mock.Of<IBodyConverterFactory>(),
                Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>());
        }

        private static TransactionMode ReadTransactionMode(SqlServiceBrokerReceiver receiver)
        {
            FieldInfo field = typeof(SqlServiceBrokerReceiver)
                .GetField(TransactionModeFieldName, BindingFlags.NonPublic | BindingFlags.Instance);

            field.Should().NotBeNull(
                $"production field '{TransactionModeFieldName}' must exist; a rename breaks this contract test");

            return (TransactionMode)field.GetValue(receiver);
        }

        private static ReceiverOptions BuildOptions(TransactionMode? perReceiverMode)
            => new ReceiverOptions
            {
                MessageReceiverPath = "test-queue",
                TransactionMode = perReceiverMode,
            };

        // INVARIANT: per-receiver None overrides the ctor-captured global of ReceiveOnly so the
        // receiver commits without starting a SQL transaction.
        [Fact]
        public async Task MustApplyNoneWhenPerReceiverModeIsNone()
        {
            var receiver = CreateReceiver(globalMode: TransactionMode.ReceiveOnly);

            await receiver.InitializeAsync(BuildOptions(TransactionMode.None), CancellationToken.None);

            ReadTransactionMode(receiver).Should().Be(TransactionMode.None,
                "per-receiver None must win over the global ReceiveOnly default");
        }

        // INVARIANT: per-receiver ReceiveOnly is preserved explicitly so callers can override a
        // global None back to transactional without relying on the ctor default.
        [Fact]
        public async Task MustApplyReceiveOnlyWhenPerReceiverModeIsReceiveOnly()
        {
            var receiver = CreateReceiver(globalMode: TransactionMode.None);

            await receiver.InitializeAsync(BuildOptions(TransactionMode.ReceiveOnly), CancellationToken.None);

            ReadTransactionMode(receiver).Should().Be(TransactionMode.ReceiveOnly,
                "per-receiver ReceiveOnly must win over a global None");
        }

        // INVARIANT: null per-receiver TransactionMode leaves the ctor-captured global intact.
        // The ctor default when no MessageBrokerOptions is supplied is ReceiveOnly.
        [Fact]
        public async Task MustInheritCtorCapturedGlobalWhenPerReceiverModeIsNull()
        {
            // Pass null brokerOptions so the ctor default (ReceiveOnly) is the global.
            var receiver = CreateReceiver(globalMode: null);

            await receiver.InitializeAsync(BuildOptions(perReceiverMode: null), CancellationToken.None);

            ReadTransactionMode(receiver).Should().Be(TransactionMode.ReceiveOnly,
                "null per-receiver TransactionMode must leave the ctor-captured default (ReceiveOnly) intact");
        }
    }
}
