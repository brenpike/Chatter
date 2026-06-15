using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Inbox;
using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosInboxDeduplicator
{
    public class WhenDeduplicating : Testing.Core.Context
    {
        [Fact]
        public async Task MustThrowNotSupportedBecauseDocumentTierDedupsViaBatchMarkerNotHasBeenReceived()
        {
            // The Cosmos document tier realizes IInboxDeduplicator as a type for parity but does NOT dedup via a
            // HasBeenReceived read (that would be the TOCTOU the co-resident-marker + 409-at-execute design eliminates).
            // The shim is non-DI-registered; invoking HasBeenReceived is a programming error.
            IInboxDeduplicator deduplicator = new CosmosInboxDeduplicator();

            Func<Task> act = () => deduplicator.HasBeenReceived("msg-1", CancellationToken.None);

            await act.Should().ThrowAsync<NotSupportedException>();
        }
    }
}
