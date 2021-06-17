using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.Pipeline.UsingCommandPipelineBuilder
{
    public class WhenInitializing
    {
        private readonly Mock<IServiceCollection> _serviceCollection;

        public WhenInitializing()
            => _serviceCollection = new Mock<IServiceCollection>();

        [Fact]
        public void MustThrowWhenServiceCollectionIsNull()
        {
            Action ctor = () => new CommandPipelineBuilder(null);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustNotThrowWhenServiceCollectionHasValue()
        {
            Action ctor = () => new CommandPipelineBuilder(_serviceCollection.Object);
            ctor.Should().NotThrow<ArgumentNullException>();
            ctor.Should().NotThrow();
        }
    }
}
