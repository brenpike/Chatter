using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingAadTokenProviderFactory
{
    public class WhenCreating : global::Chatter.Testing.Core.Context
    {
        [Fact]
        public void MustReturnNonNullFactory()
        {
            var factory = AadTokenProviderFactory.Create("client-id");

            factory.Should().NotBeNull();
            factory.Should().BeOfType<AadTokenProviderFactory>();
        }

        [Fact]
        public void MustNotThrowWhenClientIdIsNull()
        {
            Action create = () => AadTokenProviderFactory.Create(null);

            create.Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowWhenClientIdIsEmpty()
        {
            Action create = () => AadTokenProviderFactory.Create(string.Empty);

            create.Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowWhenClientIdIsWhitespace()
        {
            Action create = () => AadTokenProviderFactory.Create("   ");

            create.Should().NotThrow();
        }

        [Fact]
        public void MustReturnNonNullFactoryWhenClientIdIsNull()
        {
            var factory = AadTokenProviderFactory.Create(null);

            factory.Should().NotBeNull();
        }
    }
}
