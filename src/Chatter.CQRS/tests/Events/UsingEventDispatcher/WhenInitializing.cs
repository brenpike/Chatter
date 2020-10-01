using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.Events.UsingEventDispatcher
{
    public class WhenInitializing : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly LoggerCreator<EventDispatcher> _logger;

        public WhenInitializing() 
            => _logger = New.Common().Logger<EventDispatcher>();

        [Fact]
        public void MustThrowWhenServiceProviderIsNull()
        {
            Action ctor = () => new EventDispatcher(null, _logger.Creation);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenLoggerIsNull()
        {
            Action ctor = () => new EventDispatcher(_serviceProvider.Object, null);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustNotThrowWhenServiceProviderAndLoggerHaveValue()
        {
            Action ctor = () => new EventDispatcher(_serviceProvider.Object, _logger.Creation);
            ctor.Should().NotThrow<ArgumentNullException>();
            ctor.Should().NotThrow();
        }

        [Fact]
        public void MustThrowWhenServiceProviderAndLoggerAreNull()
        {
            Action ctor = () => new EventDispatcher(null, null);
            ctor.Should().Throw<ArgumentNullException>();
        }
    }
}
