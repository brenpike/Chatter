using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Re-arm proof for the SqlChangeFeed integration suite (STEP-C6). A single SQL Service Broker conversation
    // delivers ONE message and then must be re-subscribed for the next change. This test drives TWO sequential
    // mutations through the REAL change-feed path and asserts the receiver delivered BOTH — proving the
    // subscribe -> notify -> re-subscribe cycle, not a one-shot delivery or a replay of the first message.
    //
    // Gated by [RequiresDockerFact]. Owns a DISTINCT row DTO + table (object names derive from typeof(TRow).Name).
    [Trait("Category", "Integration")]
    [Collection(SqlChangeFeedCollection.Name)]
    public class ChangeFeedRearmTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private const string TableName = "ChangeFeedRearm";

        private readonly SqlChangeFeedFixture _fixture;

        public ChangeFeedRearmTests(SqlChangeFeedFixture fixture)
            => _fixture = fixture;

        public sealed class RearmRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedReArmsAndDeliversASecondNotificationAfterTheFirst()
        {
            var connectionString = _fixture.GetAppConnectionString();
            await ChangeFeedTableProvisioning.CreateTableAsync(connectionString, TableName, CancellationToken.None);

            var harness = ChatterChangeFeedHarness<RearmRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                TableName);
            try
            {
                await harness.RunMigrationsAsync();
                await harness.StartAsync();

                // 1st mutation -> await the 1st invocation.
                await ChangeFeedTableProvisioning.InsertRowAsync(connectionString, TableName, 1, "first", "first-value", CancellationToken.None);
                var first = await harness.Signals.WaitForHandledAsync<RowInsertedEvent<RearmRow>>(HandlerWait);
                first.Inserted.Name.Should().Be("first");

                // 2nd mutation -> the receiver must re-arm and deliver a SECOND insert event.
                await ChangeFeedTableProvisioning.InsertRowAsync(connectionString, TableName, 2, "second", "second-value", CancellationToken.None);
                var observedCount = await harness.Signals.WaitForInvocationCountAsync<RowInsertedEvent<RearmRow>>(2, HandlerWait);
                observedCount.Should().BeGreaterThanOrEqualTo(2, "the change feed must re-subscribe and deliver the second mutation");

                // The second delivery must reflect the SECOND mutation's payload — proving a fresh notification,
                // not a replay of the first message.
                var secondRecord = harness.Signals.GetOrAdd<RowInsertedEvent<RearmRow>>().Records
                    .FirstOrDefault(e => e.Inserted != null && e.Inserted.Id == 2);
                secondRecord.Should().NotBeNull("the re-armed delivery must carry the second mutation, not a replay of the first");
                secondRecord.Inserted.Name.Should().Be("second");
                secondRecord.Inserted.Value.Should().Be("second-value");
            }
            finally
            {
                await harness.DisposeAsync();
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, TableName, CancellationToken.None);
            }
        }
    }
}
