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
    // still in flight). When the rental is then disposed it calls back into ReturnPublishChannel, which must
    // dispose the orphaned channel WITHOUT touching the publish-pool semaphore — the pool is gone.
    //
    // These tests pin the OBSERVABLE dispose contract: no-throw on rental-return-after-dispose, the orphaned
    // channel is disposed, and DisposeAsync is idempotent. The recovery-after-dispose no-op is pinned alongside
    // the recovery suite (WhenRecreatingReceiveChannelOnRecovery.MustNotThrowWhenRecoveryFiresAfterDisposal) via
    // the same InternalsVisibleTo reflection seam. The full dispose-vs-in-flight-settle and dispose-vs-recovery
    // races under REAL concurrency are only provable against a live broker on the nightly Docker suite; these
    // broker-free tests pin the observable contract (no-throw, idempotent, orphan-disposed) that the
    // gate-serialized teardown and the never-disposed gates (GATE LIFETIME) are designed to uphold.
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

        // DisposeAsync is idempotent: a second call short-circuits on _disposed and is a clean no-op. The gates are
        // never disposed (GATE LIFETIME), so re-entry never touches a disposed semaphore.
        [Fact]
        public async Task MustNotThrowWhenDisposedTwice()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            await source.DisposeAsync();

            Func<Task> disposeAgain = async () => await source.DisposeAsync();

            await disposeAgain.Should().NotThrowAsync();
        }
    }
}
