using System;
using System.Collections.Generic;
using System.Text;
using Chatter.CQRS.Commands;
using Chatter.Testing.Core;
using Moq;
using Xunit;

namespace Chatter.CQRS.Tests.Commands.UsingCommandDispatcher
{
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly Mock<IMessageHandler<IMessage>> _handler = new Mock<IMessageHandler<IMessage>>();
        private readonly CommandDispatcher _sut;

        public WhenDispatching()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IMessageHandler<IMessage>)))
                .Returns(_handler.Object);
            _sut = new CommandDispatcher(_serviceProvider.Object);
        }

        [Fact]
        public async void MustGetMessageHandler()
        {
            await _sut.Dispatch<IMessage>(null, null);
            _serviceProvider.Verify(p=> p.GetService(typeof(IMessageHandler<IMessage>)), Times.Once);
        }
    }
}
