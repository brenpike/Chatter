using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using FluentAssertions;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqConnectionSource
{
    // Pins the shutdown-ordering contract between RabbitMqConnectionSource and an outstanding
    // RabbitMqPublishChannelRental: a rental can outlive the source (the source is disposed while a publish is
    // still in flight). When the rental is then disposed it calls back into ReturnPublishChannel, which must NOT
    // touch the publish-pool semaphore the source already disposed — otherwise the rental's DisposeAsync throws
    // ObjectDisposedException. The orphaned channel must still be disposed.
    public class WhenDisposing : Testing.Core.Context
    {
        [Fact]
        public async Task MustNotThrowWhenRentalIsDisposedAfterSource()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(), publishChannelPoolCapacity: 1);
            var channel = new RecordingChannel();
            var rental = new RabbitMqPublishChannelRental(source, channel);

            // Source disposed FIRST (disposes the publish-pool semaphore), rental disposed AFTER.
            await source.DisposeAsync();

            Func<Task> disposeRental = async () => await rental.DisposeAsync();

            await disposeRental.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustDisposeOrphanedChannelWhenRentalReturnedAfterSourceDisposed()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(), publishChannelPoolCapacity: 1);
            var channel = new RecordingChannel();
            var rental = new RabbitMqPublishChannelRental(source, channel);

            await source.DisposeAsync();
            await rental.DisposeAsync();

            channel.Disposed.Should().BeTrue();
        }
    }
}
