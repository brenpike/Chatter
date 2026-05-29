using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Context.UsingTransactionContext
{
    public class WhenConstructing : Testing.Core.Context
    {
        [Fact]
        public void MustDefaultReceiverToNullForParameterlessConstructor()
            => new TransactionContext().TransactionReceiver.Should().BeNull();

        [Fact]
        public void MustDefaultModeToReceiveOnlyForParameterlessConstructor()
            => new TransactionContext().TransactionMode.Should().Be(TransactionMode.ReceiveOnly);

        [Fact]
        public void MustMapReceiverForSingleArgConstructor()
            => new TransactionContext("receiver").TransactionReceiver.Should().Be("receiver");

        [Fact]
        public void MustDefaultModeToReceiveOnlyForSingleArgConstructor()
            => new TransactionContext("receiver").TransactionMode.Should().Be(TransactionMode.ReceiveOnly);

        [Fact]
        public void MustMapReceiverForTwoArgConstructor()
            => new TransactionContext("receiver", TransactionMode.FullAtomicityViaInfrastructure).TransactionReceiver.Should().Be("receiver");

        [Fact]
        public void MustMapModeForTwoArgConstructor()
            => new TransactionContext("receiver", TransactionMode.FullAtomicityViaInfrastructure)
                .TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);

        [Fact]
        public void MustExposeContextContainer()
            => new TransactionContext().Container.Should().NotBeNull();
    }
}
