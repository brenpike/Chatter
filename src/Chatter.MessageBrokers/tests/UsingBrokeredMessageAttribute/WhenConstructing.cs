using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingBrokeredMessageAttribute
{
    public class WhenConstructing : Testing.Core.Context
    {
        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("   ", "   ")]
        [InlineData(null, "   ")]
        [InlineData("   ", null)]
        public void MustThrowArgumentExceptionWhenBothSendingAndReceivingPathAreNullOrWhitespace(string sendingPath, string receivingPath)
        {
            FluentActions.Invoking(() => new BrokeredMessageAttribute(sendingPath, receivingPath))
                .Should().Throw<ArgumentException>();
        }

        [Fact]
        public void MustNotThrowWhenOnlyReceivingPathSupplied()
        {
            FluentActions.Invoking(() => new BrokeredMessageAttribute(null, "receiving"))
                .Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowWhenOnlySendingPathSupplied()
        {
            FluentActions.Invoking(() => new BrokeredMessageAttribute("sending"))
                .Should().NotThrow();
        }

        [Fact]
        public void MustMapSendingPathArgToSendingPathProperty()
        {
            var attribute = new BrokeredMessageAttribute("sending");
            attribute.SendingPath.Should().Be("sending");
        }

        [Fact]
        public void MustMapReceivingPathArgToReceiverNameProperty()
        {
            var attribute = new BrokeredMessageAttribute("sending", "receiving");
            attribute.ReceiverName.Should().Be("receiving");
        }

        [Fact]
        public void MustMapErrorQueueNameArg()
        {
            var attribute = new BrokeredMessageAttribute("sending", errorQueueName: "errorQueue");
            attribute.ErrorQueueName.Should().Be("errorQueue");
        }

        [Fact]
        public void MustMapMessageDescriptionArg()
        {
            var attribute = new BrokeredMessageAttribute("sending", messageDescription: "description");
            attribute.MessageDescription.Should().Be("description");
        }

        [Fact]
        public void MustMapInfrastructureTypeArg()
        {
            var attribute = new BrokeredMessageAttribute("sending", infrastructureType: "infra");
            attribute.InfrastructureType.Should().Be("infra");
        }

        [Fact]
        public void MustMapDeadletterQueueNameArg()
        {
            var attribute = new BrokeredMessageAttribute("sending", deadletterQueueName: "deadletter");
            attribute.DeadletterQueueName.Should().Be("deadletter");
        }

        [Fact]
        public void MustDefaultInfrastructureTypeToEmptyStringWhenNotSupplied()
        {
            var attribute = new BrokeredMessageAttribute("sending");
            attribute.InfrastructureType.Should().Be(string.Empty);
        }

        [Fact]
        public void MustDefaultReceiverNameToNullWhenReceivingPathNotSupplied()
        {
            var attribute = new BrokeredMessageAttribute("sending");
            attribute.ReceiverName.Should().BeNull();
        }

        [Fact]
        public void MustDefaultErrorQueueNameToNullWhenNotSupplied()
        {
            var attribute = new BrokeredMessageAttribute("sending");
            attribute.ErrorQueueName.Should().BeNull();
        }

        [Fact]
        public void MustDefaultMessageDescriptionToNullWhenNotSupplied()
        {
            var attribute = new BrokeredMessageAttribute("sending");
            attribute.MessageDescription.Should().BeNull();
        }

        [Fact]
        public void MustDefaultDeadletterQueueNameToNullWhenNotSupplied()
        {
            var attribute = new BrokeredMessageAttribute("sending");
            attribute.DeadletterQueueName.Should().BeNull();
        }
    }
}
