using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Recovery;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.UsingCriticalFailureEventDispatcher
{
    public class WhenNotifying : Testing.Core.Context
    {
        private readonly Mock<IMessageDispatcher> _messageDispatcher = new Mock<IMessageDispatcher>();
        private readonly RecordingLoggerCreator<CriticalFailureEventDispatcher> _logger;

        public WhenNotifying()
            => _logger = New.Common().RecordingLogger<CriticalFailureEventDispatcher>();

        // INVARIANT: IServiceScopeFactory.CreateScope cannot be mocked with this Moq/DI-abstractions combination
        // (Moq reports it as non-overridable). A real DI scope factory is therefore used as the framework seam, and
        // the domain leaf collaborator (IMessageDispatcher) is the mock whose interaction is verified.
        private CriticalFailureEventDispatcher CreateSut(bool registerDispatcher)
        {
            var services = new ServiceCollection();
            if (registerDispatcher)
            {
                services.AddSingleton(_messageDispatcher.Object);
            }
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            return new CriticalFailureEventDispatcher(scopeFactory, _logger.Creation);
        }

        private FailureContext CreateFailureContext()
            => New.MessageBrokers().FailureContext().Creation;

        [Fact]
        public void MustThrowWhenScopeFactoryIsNull()
            => FluentActions.Invoking(() => new CriticalFailureEventDispatcher(null, _logger.Creation))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenLoggerIsNull()
        {
            var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            FluentActions.Invoking(() => new CriticalFailureEventDispatcher(scopeFactory, null))
                .Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public async Task MustDispatchCriticalFailureEventWhenDispatcherResolved()
        {
            var sut = CreateSut(registerDispatcher: true);

            await sut.Notify(CreateFailureContext());

            _messageDispatcher.Verify(d => d.Dispatch(It.IsAny<CriticalFailureEvent>()), Times.Once);
        }

        [Fact]
        public async Task MustDispatchCriticalFailureEventCarryingFailureContext()
        {
            CriticalFailureEvent dispatched = null;
            _messageDispatcher.Setup(d => d.Dispatch(It.IsAny<CriticalFailureEvent>()))
                              .Callback<CriticalFailureEvent>(e => dispatched = e)
                              .Returns(Task.CompletedTask);
            var sut = CreateSut(registerDispatcher: true);
            var failureContext = CreateFailureContext();

            await sut.Notify(failureContext);

            dispatched.Context.Should().BeSameAs(failureContext);
        }

        [Fact]
        public async Task MustNotDispatchWhenNoDispatcherRegistered()
        {
            var sut = CreateSut(registerDispatcher: false);

            await sut.Notify(CreateFailureContext());

            _messageDispatcher.Verify(d => d.Dispatch(It.IsAny<CriticalFailureEvent>()), Times.Never);
        }
    }
}
