using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Chatter.CQRS.Tests.UsingMessageDispatcherProvider
{
    public class WhenGettingDispatcher
    {
        private readonly MessageDispatcherProvider _sut;
        private readonly Mock<IDispatchMessages> _nonRetrievableEventDispatcher;
        private readonly Mock<IDispatchMessages> _anotherEventDispatcher;
        private readonly Mock<IDispatchMessages> _eventDispatcher;
        private readonly Mock<IDispatchMessages> _commandDispatcher;

        public WhenGettingDispatcher()
        {
            var dispatchers = new Mock<IEnumerable<IDispatchMessages>>();

            _eventDispatcher = new Mock<IDispatchMessages>();
            _eventDispatcher.SetupGet(d => d.DispatchType).Returns(typeof(IEvent));

            _nonRetrievableEventDispatcher = new Mock<IDispatchMessages>();
            _nonRetrievableEventDispatcher.SetupGet(d => d.DispatchType).Returns(typeof(IEvent));

            _anotherEventDispatcher = new Mock<IDispatchMessages>();
            _anotherEventDispatcher.SetupGet(d => d.DispatchType).Returns(typeof(IFakeInterface));

            _commandDispatcher = new Mock<IDispatchMessages>();
            _commandDispatcher.SetupGet(d => d.DispatchType).Returns(typeof(ICommand));

            dispatchers.Setup(d => d.GetEnumerator())
                .Returns(new IDispatchMessages[]
                {
                    _nonRetrievableEventDispatcher.Object,
                    _eventDispatcher.Object,
                    _anotherEventDispatcher.Object,
                    _commandDispatcher.Object
                }.ToList().GetEnumerator());

            _sut = new MessageDispatcherProvider(dispatchers.Object);
        }

        [Fact]
        public void MustGetDispatcherByExactType()
        {
            var dispatcher = _sut.GetDispatcher<ICommand>();
            dispatcher.Should().NotBeNull();
            dispatcher.Should().BeSameAs(_commandDispatcher.Object);
        }

        [Fact]
        public void MustGetDispatcherByDispatchTypeWithMatchingImplementedInterface()
        {
            var dispatcher = _sut.GetDispatcher<FakeEvent>();
            dispatcher.Should().NotBeNull();
            dispatcher.Should().BeSameAs(_eventDispatcher.Object);
            dispatcher.Should().NotBeSameAs(_nonRetrievableEventDispatcher.Object);
        }

        [Fact]
        public void MustThrowWhenNoMatchingDispatchTypeIfIncludedInConstructor()
            => FluentActions.Invoking(() => _sut.GetDispatcher<IMessage>()).Should().ThrowExactly<KeyNotFoundException>();

        [Fact]
        public void MustGetDispatcherByDispatchTypeThatMatchesFirstMatchingImplementedInterface()
        {
            var dispatcher = _sut.GetDispatcher<FakeEventThatImplementsMany>();
            dispatcher.Should().NotBeNull();
            dispatcher.Should().BeSameAs(_anotherEventDispatcher.Object);
        }

        private class FakeEvent : IEvent { }
        private class FakeEventThatImplementsMany : IFakeInterface, IEvent { }
        private interface IFakeInterface { }
    }
}
