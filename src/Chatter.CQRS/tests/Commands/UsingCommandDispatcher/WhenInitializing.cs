using Chatter.CQRS.Commands;
using FluentAssertions;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.Commands.UsingCommandDispatcher
{
    public class WhenInitializing : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();

        public WhenInitializing()
        { }

        [Fact]
        public void MustThrowWhenServiceProviderIsNull()
        {
            Action ctor = () => new CommandDispatcher(null);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustNotThrowWhenServiceProviderHasValue()
        {
            Action ctor = () => new CommandDispatcher(_serviceProvider.Object);
            ctor.Should().NotThrow<ArgumentNullException>();
            ctor.Should().NotThrow();
        }
    }
}
