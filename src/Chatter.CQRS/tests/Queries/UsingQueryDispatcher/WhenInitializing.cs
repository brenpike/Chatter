using Chatter.CQRS.Queries;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.Queries.UsingQueryDIspatcher
{
    public class WhenInitializing : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly LoggerCreator<QueryDispatcher> _logger;

        public WhenInitializing()
            => _logger = New.Common().Logger<QueryDispatcher>();

        [Fact]
        public void MustThrowWhenServiceProviderIsNull()
        {
            Action ctor = () => new QueryDispatcher(null, _logger.Creation);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenLoggerIsNull()
        {
            Action ctor = () => new QueryDispatcher(_serviceProvider.Object, null);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustNotThrowWhenServiceProviderAndLoggerHaveValue()
        {
            Action ctor = () => new QueryDispatcher(_serviceProvider.Object, _logger.Creation);
            ctor.Should().NotThrow<ArgumentNullException>();
            ctor.Should().NotThrow();
        }

        [Fact]
        public void MustThrowWhenServiceProviderAndLoggerAreNull()
        {
            Action ctor = () => new QueryDispatcher(null, null);
            ctor.Should().Throw<ArgumentNullException>();
        }
    }
}
