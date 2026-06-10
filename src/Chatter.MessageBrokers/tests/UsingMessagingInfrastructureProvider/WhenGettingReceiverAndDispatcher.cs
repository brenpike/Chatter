using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingMessagingInfrastructureProvider
{
    public class WhenGettingReceiverAndDispatcher : Testing.Core.Context
    {
        private readonly Mock<IMessagingInfrastructure> _infrastructure = new Mock<IMessagingInfrastructure>();
        private readonly Mock<IMessagingInfrastructureReceiver> _receiver = new Mock<IMessagingInfrastructureReceiver>();
        private readonly Mock<IMessagingInfrastructureDispatcher> _dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
        private readonly Mock<ILogger<MessagingInfrastructureProvider>> _logger = new Mock<ILogger<MessagingInfrastructureProvider>>();
        private readonly MessagingInfrastructureProvider _sut;

        public WhenGettingReceiverAndDispatcher()
        {
            _infrastructure.SetupGet(i => i.Type).Returns("test");
            _infrastructure.SetupGet(i => i.ReceiveInfrastructure).Returns(_receiver.Object);
            _infrastructure.SetupGet(i => i.DispatchInfrastructure).Returns(_dispatcher.Object);

            _sut = new MessagingInfrastructureProvider(
                new List<IMessagingInfrastructure> { _infrastructure.Object },
                _logger.Object);
        }

        [Fact]
        public void MustReturnReceiveInfrastructureForType()
            => _sut.GetReceiver("test").Should().BeSameAs(_receiver.Object);

        [Fact]
        public void MustReturnDispatchInfrastructureForType()
            => _sut.GetDispatcher("test").Should().BeSameAs(_dispatcher.Object);
    }
}
