using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Chatter.CQRS.Tests.UsingMessageDispatcherProvider
{
    public class WhenInitializing
    {
        [Fact]
        public void MustNotThrowWhenEnumerableOfDispatchersIsNull()
            => FluentActions.Invoking(() => new MessageDispatcherProvider(null)).Should().ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenEnumerableOfDispatchersContainsDispatcherWithNullDispatchType()
        {
            var dispatchers = new Mock<IEnumerable<IDispatchMessages>>();
            var dispatcherWithNullDispatchType = new Mock<IDispatchMessages>();
            dispatcherWithNullDispatchType.SetupGet(d => d.DispatchType).Returns(value: null);
            dispatchers.Setup(d => d.GetEnumerator()).Returns(new IDispatchMessages[] { dispatcherWithNullDispatchType.Object }.ToList().GetEnumerator());
            FluentActions.Invoking(() => new MessageDispatcherProvider(dispatchers.Object)).Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void MustNotThrowWhenNonNullEnumerableOfDispatchersIsProvidedToConstructor()
        {
            var dispatchers = new Mock<IEnumerable<IDispatchMessages>>();
            var eventDispatcher = new Mock<IDispatchMessages>();
            eventDispatcher.SetupGet(d => d.DispatchType).Returns(typeof(IEvent));
            var commandDispatcher = new Mock<IDispatchMessages>();
            commandDispatcher.SetupGet(d => d.DispatchType).Returns(typeof(ICommand));
            dispatchers.Setup(d => d.GetEnumerator()).Returns(new IDispatchMessages[] { eventDispatcher.Object, commandDispatcher.Object }.ToList().GetEnumerator());
            FluentActions.Invoking(() => new MessageDispatcherProvider(dispatchers.Object)).Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowWhenEnumerableOfDispatchersIsEmpty()
        {
            var dispatchers = new Mock<IEnumerable<IDispatchMessages>>();
            dispatchers.Setup(d => d.GetEnumerator()).Returns(new IDispatchMessages[] { }.ToList().GetEnumerator());
            FluentActions.Invoking(() => new MessageDispatcherProvider(dispatchers.Object)).Should().NotThrow();
        }
    }
}
