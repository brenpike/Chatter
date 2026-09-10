using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.UsingMessageDispatcher
{
    public class WhenDispatchingConcurrently
    {
        private const int ConcurrentDispatchCount = 64;

        private readonly MessageDispatcher _sut;
        private readonly Mock<IExternalDispatcher> _externalDispatcher;
        private readonly ConcurrentBag<ContextContainer> _suppliedContainers;

        public WhenDispatchingConcurrently()
        {
            _suppliedContainers = new ConcurrentBag<ContextContainer>();
            var dispatchMessages = new Mock<IDispatchMessages>();
            dispatchMessages
                .Setup(d => d.Dispatch(It.IsAny<ConcurrentFakeEvent>(), It.IsAny<IMessageHandlerContext>()))
                .Callback<ConcurrentFakeEvent, IMessageHandlerContext>((_, context) => _suppliedContainers.Add(context.Container))
                .Returns(Task.CompletedTask);
            var messageDispatcherProvider = new Mock<IMessageDispatcherProvider>();
            messageDispatcherProvider.Setup(m => m.GetDispatcher<ConcurrentFakeEvent>()).Returns(dispatchMessages.Object);
            _externalDispatcher = new Mock<IExternalDispatcher>();
            _sut = new MessageDispatcher(messageDispatcherProvider.Object, _externalDispatcher.Object);
        }

        [Fact]
        public async Task MustSupplyEveryConcurrentDispatchItsOwnContextContainer()
        {
            var dispatches = Enumerable
                .Range(0, ConcurrentDispatchCount)
                .Select(_ => Task.Run(() => _sut.Dispatch(new ConcurrentFakeEvent())))
                .ToArray();

            Func<Task> act = () => Task.WhenAll(dispatches);

            await act.Should().NotThrowAsync();
            _suppliedContainers.Should().HaveCount(ConcurrentDispatchCount);
            _suppliedContainers.Cast<object>().Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(ConcurrentDispatchCount);
            _suppliedContainers.Should().OnlyContain(container => container.Get<IExternalDispatcher>() == _externalDispatcher.Object);
            _suppliedContainers.Should().OnlyContain(container => container.Get<IMessageDispatcher>() == _sut);
        }

        private class ConcurrentFakeEvent : IEvent { }
    }
}
