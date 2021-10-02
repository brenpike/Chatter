using Chatter.CQRS.Commands;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.CQRS.Tests.Pipeline.UsingCommandBehaviorPipeline
{
    public class WhenInitializing : Testing.Core.Context
    {
        public class FakeCommand : ICommand { }

        public WhenInitializing()
        { }

        [Fact]
        public void MustThrowWhenEnumerableOfCommandBehaviorsForMessageTypeIsNull()
        {
            Action ctor = () => new CommandBehaviorPipeline<FakeCommand>(null);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustNotThrowWhenEnumerableOfCommandBehaviorsForMessageTypeHasValue()
        {
            var commands = new Mock<List<ICommandBehavior<FakeCommand>>>();
            Action ctor = () => new CommandBehaviorPipeline<FakeCommand>(commands.Object);
            ctor.Should().NotThrow<ArgumentNullException>();
            ctor.Should().NotThrow();
        }
    }
}
