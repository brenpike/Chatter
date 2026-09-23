using FluentAssertions;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingMessageContext
{
    /// <summary>
    /// The raw-dictionary Message Context Read: the same kind test <c>OutboundBrokeredMessage.TryGetMessageContextByKey</c>
    /// applies, for a reader that holds only the dictionary (ADR-0036).
    /// </summary>
    public class WhenReadingAMessageContextValueByKind : Testing.Core.Context
    {
        private const string Key = "Chatter.Probe";

        [Fact]
        public void MustReadAPresentMatchingKindThroughTheDictionaryTryRead()
        {
            var context = new Dictionary<string, object> { [Key] = "stored" };

            var found = context.TryReadMessageContext<string>(Key, out var value);

            found.Should().BeTrue();
            value.Should().Be("stored");
        }

        [Fact]
        public void MustReadAMismatchedKindAsNotFoundThroughTheDictionaryTryRead()
        {
            var context = new Dictionary<string, object> { [Key] = 7L };

            var found = context.TryReadMessageContext<string>(Key, out var value);

            found.Should().BeFalse();
            value.Should().BeNull();
        }

        [Fact]
        public void MustReadAnAbsentKeyAsNotFoundThroughTheDictionaryTryRead()
        {
            var context = new Dictionary<string, object>();

            var found = context.TryReadMessageContext<string>(Key, out var value);

            found.Should().BeFalse();
            value.Should().BeNull();
        }

        [Fact]
        public void MustReadAStoredNullAsNotFoundThroughTheDictionaryTryRead()
        {
            var context = new Dictionary<string, object> { [Key] = null };

            var found = context.TryReadMessageContext<string>(Key, out var value);

            found.Should().BeFalse();
            value.Should().BeNull();
        }
    }
}
