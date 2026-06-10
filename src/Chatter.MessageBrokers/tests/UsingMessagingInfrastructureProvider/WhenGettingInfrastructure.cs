using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingMessagingInfrastructureProvider
{
    public class WhenGettingInfrastructure : Testing.Core.Context
    {
        private readonly Mock<IMessagingInfrastructure> _firstInfrastructure = new Mock<IMessagingInfrastructure>();
        private readonly Mock<IMessagingInfrastructure> _secondInfrastructure = new Mock<IMessagingInfrastructure>();
        private readonly Mock<ILogger<MessagingInfrastructureProvider>> _logger = new Mock<ILogger<MessagingInfrastructureProvider>>();

        public WhenGettingInfrastructure()
        {
            _firstInfrastructure.SetupGet(i => i.Type).Returns("first");
            _secondInfrastructure.SetupGet(i => i.Type).Returns("second");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void MustReturnDefaultWhenTypeIsNullOrWhitespace(string type)
        {
            var sut = new MessagingInfrastructureProvider(
                new List<IMessagingInfrastructure> { _firstInfrastructure.Object, _secondInfrastructure.Object },
                _logger.Object);

            sut.GetInfrastructure(type).Should().BeSameAs(_firstInfrastructure.Object);
        }

        [Fact]
        public void MustResolveInfrastructureByType()
        {
            var sut = new MessagingInfrastructureProvider(
                new List<IMessagingInfrastructure> { _firstInfrastructure.Object, _secondInfrastructure.Object },
                _logger.Object);

            sut.GetInfrastructure("second").Should().BeSameAs(_secondInfrastructure.Object);
        }

        [Fact]
        public void MustThrowKeyNotFoundWhenTypeIsUnknown()
        {
            var sut = new MessagingInfrastructureProvider(
                new List<IMessagingInfrastructure> { _firstInfrastructure.Object },
                _logger.Object);

            FluentActions.Invoking(() => sut.GetInfrastructure("unknown"))
                .Should().Throw<KeyNotFoundException>();
        }

        [Fact]
        public void MustThrowInvalidOperationWhenNoInfrastructureRegistered()
        {
            var sut = new MessagingInfrastructureProvider(
                new List<IMessagingInfrastructure>(),
                _logger.Object);

            FluentActions.Invoking(() => sut.GetInfrastructure("anything"))
                .Should().Throw<InvalidOperationException>();
        }
    }
}
