using FluentAssertions;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.UsingMessageDispatcher
{
    public class WhenInitializing
    {
        private readonly Mock<IMessageDispatcherProvider> _messageDispatcherProvider;
        private readonly Mock<IExternalDispatcher> _externalDispatcher;

        public WhenInitializing()
        {
            _messageDispatcherProvider = new Mock<IMessageDispatcherProvider>();
            _externalDispatcher = new Mock<IExternalDispatcher>();
        }

        [Fact]
        public void MustThrowWhenMessageDispatcherProviderIsNull()
            => FluentActions.Invoking(() => new MessageDispatcher(null, _externalDispatcher.Object)).Should().ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenExternalDispatcherIsNull()
            => FluentActions.Invoking(() => new MessageDispatcher(_messageDispatcherProvider.Object, null)).Should().ThrowExactly<ArgumentNullException>();
    }
}
