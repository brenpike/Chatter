using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using FluentAssertions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingChangeFeedMessages
{
    public class WhenConstructing : Testing.Core.Context
    {
        [Fact]
        public void MustConstructChangeFeedItemParameterlessly()
            => FluentActions.Invoking(() => new ChangeFeedItem<FakeRowData>())
                .Should().NotThrow();

        [Fact]
        public void MustDefaultChangeFeedItemInsertedToNull()
            => new ChangeFeedItem<FakeRowData>().Inserted.Should().BeNull();

        [Fact]
        public void MustDefaultChangeFeedItemDeletedToNull()
            => new ChangeFeedItem<FakeRowData>().Deleted.Should().BeNull();

        [Fact]
        public void MustRoundTripChangeFeedItemInserted()
        {
            var row = new FakeRowData { Id = 1, Name = "inserted" };
            new ChangeFeedItem<FakeRowData> { Inserted = row }.Inserted.Should().BeSameAs(row);
        }

        [Fact]
        public void MustRoundTripChangeFeedItemDeleted()
        {
            var row = new FakeRowData { Id = 2, Name = "deleted" };
            new ChangeFeedItem<FakeRowData> { Deleted = row }.Deleted.Should().BeSameAs(row);
        }

        [Fact]
        public void MustImplementICommandOnChangeFeedItem()
            => new ChangeFeedItem<FakeRowData>().Should().BeAssignableTo<ICommand>();

        [Fact]
        public void MustDefaultProcessChangeFeedCommandChangesToNonNullEmptyList()
            => new ProcessChangeFeedCommand<FakeRowData>().Changes
                .Should().NotBeNull().And.BeEmpty();

        [Fact]
        public void MustRoundTripProcessChangeFeedCommandChanges()
        {
            var changes = new[] { new ChangeFeedItem<FakeRowData>() };
            new ProcessChangeFeedCommand<FakeRowData> { Changes = changes }.Changes
                .Should().BeSameAs(changes);
        }

        [Fact]
        public void MustImplementICommandOnProcessChangeFeedCommand()
            => new ProcessChangeFeedCommand<FakeRowData>().Should().BeAssignableTo<ICommand>();

        [Fact]
        public void MustMapInsertedFromRowInsertedEventConstructor()
        {
            var row = new FakeRowData { Id = 3, Name = "ins" };
            new RowInsertedEvent<FakeRowData>(row).Inserted.Should().BeSameAs(row);
        }

        [Fact]
        public void MustImplementIEventOnRowInsertedEvent()
            => new RowInsertedEvent<FakeRowData>(new FakeRowData()).Should().BeAssignableTo<IEvent>();

        [Fact]
        public void MustNotThrowWhenRowInsertedEventPayloadIsNull()
            => FluentActions.Invoking(() => new RowInsertedEvent<FakeRowData>(null))
                .Should().NotThrow();

        [Fact]
        public void MustMapFirstConstructorArgToNewValueOnRowUpdatedEvent()
        {
            var newValue = new FakeRowData { Id = 4, Name = "new" };
            var oldValue = new FakeRowData { Id = 5, Name = "old" };
            new RowUpdatedEvent<FakeRowData>(newValue, oldValue).NewValue.Should().BeSameAs(newValue);
        }

        [Fact]
        public void MustMapSecondConstructorArgToOldValueOnRowUpdatedEvent()
        {
            var newValue = new FakeRowData { Id = 4, Name = "new" };
            var oldValue = new FakeRowData { Id = 5, Name = "old" };
            new RowUpdatedEvent<FakeRowData>(newValue, oldValue).OldValue.Should().BeSameAs(oldValue);
        }

        [Fact]
        public void MustImplementIEventOnRowUpdatedEvent()
            => new RowUpdatedEvent<FakeRowData>(new FakeRowData(), new FakeRowData())
                .Should().BeAssignableTo<IEvent>();

        [Fact]
        public void MustNotThrowWhenRowUpdatedEventPayloadsAreNull()
            => FluentActions.Invoking(() => new RowUpdatedEvent<FakeRowData>(null, null))
                .Should().NotThrow();

        [Fact]
        public void MustMapDeletedFromRowDeletedEventConstructor()
        {
            var row = new FakeRowData { Id = 6, Name = "del" };
            new RowDeletedEvent<FakeRowData>(row).Deleted.Should().BeSameAs(row);
        }

        [Fact]
        public void MustImplementIEventOnRowDeletedEvent()
            => new RowDeletedEvent<FakeRowData>(new FakeRowData()).Should().BeAssignableTo<IEvent>();

        [Fact]
        public void MustNotThrowWhenRowDeletedEventPayloadIsNull()
            => FluentActions.Invoking(() => new RowDeletedEvent<FakeRowData>(null))
                .Should().NotThrow();
    }
}
