using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingScopedReceivedMessageDispatcher
{
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly Mock<IMessageDispatcher> _messageDispatcher = new Mock<IMessageDispatcher>();
        private readonly Mock<IBrokeredMessageDispatcher> _brokeredMessageDispatcher = new Mock<IBrokeredMessageDispatcher>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();

        public WhenDispatching()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private class FakePayload : IMessage { }

        // INVARIANT: IServiceScopeFactory.CreateScope cannot be mocked with this Moq/DI-abstractions combination,
        // so a real DI scope factory is used as the framework seam. The domain leaf collaborators
        // (IMessageDispatcher, IBrokeredMessageDispatcher) are the mocks whose interaction is verified.
        private ScopedReceivedMessageDispatcher CreateSut(bool registerMessageDispatcher = true, bool registerBrokeredDispatcher = true)
        {
            var services = new ServiceCollection();
            if (registerMessageDispatcher)
            {
                services.AddSingleton(_messageDispatcher.Object);
            }
            if (registerBrokeredDispatcher)
            {
                services.AddSingleton(_brokeredMessageDispatcher.Object);
            }
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            return new ScopedReceivedMessageDispatcher(scopeFactory);
        }

        private MessageBrokerContext CreateContext()
            => new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", CancellationToken.None, _bodyConverter.Object);

        private Task Dispatch(ScopedReceivedMessageDispatcher sut, FakePayload payload, MessageBrokerContext context)
            => ((IReceivedMessageDispatcher)sut).DispatchAsync(payload, context, CancellationToken.None);

        [Fact]
        public void MustThrowWhenScopeFactoryIsNull()
            => FluentActions.Invoking(() => new ScopedReceivedMessageDispatcher(null)).Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustRelayPayloadAndContextToMessageDispatcher()
        {
            var sut = CreateSut();
            var payload = new FakePayload();
            var context = CreateContext();

            await Dispatch(sut, payload, context);

            _messageDispatcher.Verify(d => d.Dispatch(payload, context), Times.Once);
        }

        [Fact]
        public async Task MustIncludeBrokeredMessageDispatcherInContextContainer()
        {
            var sut = CreateSut();
            var context = CreateContext();

            await Dispatch(sut, new FakePayload(), context);

            context.Container.TryGet<IExternalDispatcher>(out var dispatcher).Should().BeTrue();
            dispatcher.Should().BeSameAs(_brokeredMessageDispatcher.Object);
        }

        [Fact]
        public async Task MustThrowWhenMessageDispatcherNotRegistered()
        {
            var sut = CreateSut(registerMessageDispatcher: false);

            await FluentActions.Invoking(async () => await Dispatch(sut, new FakePayload(), CreateContext()))
                .Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustThrowWhenBrokeredMessageDispatcherNotRegistered()
        {
            var sut = CreateSut(registerBrokeredDispatcher: false);

            await FluentActions.Invoking(async () => await Dispatch(sut, new FakePayload(), CreateContext()))
                .Should().ThrowAsync<InvalidOperationException>();
        }
    }
}
