using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingMessagingInfrastructureProvider
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly Mock<IMessagingInfrastructure> _firstInfrastructure = new Mock<IMessagingInfrastructure>();
        private readonly Mock<IMessagingInfrastructure> _secondInfrastructure = new Mock<IMessagingInfrastructure>();
        private readonly Mock<ILogger<MessagingInfrastructureProvider>> _logger = new Mock<ILogger<MessagingInfrastructureProvider>>();

        public WhenConstructing()
        {
            _firstInfrastructure.SetupGet(i => i.Type).Returns("first");
            _secondInfrastructure.SetupGet(i => i.Type).Returns("second");
        }

        [Fact]
        public void MustThrowWhenLoggerIsNull()
            => FluentActions.Invoking(() => new MessagingInfrastructureProvider(
                    new[] { _firstInfrastructure.Object },
                    null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustSetFirstInfrastructureAsDefault()
        {
            var sut = new MessagingInfrastructureProvider(
                new List<IMessagingInfrastructure> { _firstInfrastructure.Object, _secondInfrastructure.Object },
                _logger.Object);

            sut.GetInfrastructure(null).Should().BeSameAs(_firstInfrastructure.Object);
        }
    }
}
