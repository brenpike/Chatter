using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingMaxReceiveAttemptsExceededException
{
    public class WhenConstructing : Testing.Core.Context
    {
        [Fact]
        public void MustBeAnException()
            => new MaxReceiveAttemptsExceededException().Should().BeAssignableTo<Exception>();

        [Fact]
        public void MustUseDefaultExceptionMessageNamingDerivedType()
        {
            // INVARIANT: the type declares no constructors, so it carries the framework default
            // "Exception of type '...' was thrown." message naming the DERIVED type, and exposes
            // no custom message text or Attempts-like property.
            var sut = new MaxReceiveAttemptsExceededException();
            sut.Message.Should().Be(
                "Exception of type 'Chatter.MessageBrokers.Receiving.MaxReceiveAttemptsExceededException' was thrown.");
        }

        [Fact]
        public void MustHaveNullInnerException()
            => new MaxReceiveAttemptsExceededException().InnerException.Should().BeNull();
    }
}
