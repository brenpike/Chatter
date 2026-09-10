using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingMessageHandlersForADualRoleHandler : Testing.Core.Context
    {
        [Fact]
        public void MustRegisterDualRoleHandlerOnceForTheEventItHandles()
        {
            var sc = ScanForDualRoleHandler();

            CountDescriptors(sc, typeof(IMessageHandler<DualRoleEvent>), typeof(DualRoleHandler)).Should().Be(1);
        }

        [Fact]
        public void MustRegisterDualRoleHandlerOnceForTheCommandItHandles()
        {
            var sc = ScanForDualRoleHandler();

            CountDescriptors(sc, typeof(IMessageHandler<DualRoleCommand>), typeof(DualRoleHandler)).Should().Be(1);
        }

        [Fact]
        public async Task MustInvokeDualRoleHandlerOnceWhenTheEventIsDispatched()
        {
            var recorder = new HandlerInvocationRecorder();
            var sc = ScanForDualRoleHandler();
            sc.AddSingleton(recorder);

            using (var serviceProvider = sc.BuildServiceProvider())
            {
                var dispatcher = new EventDispatcher(serviceProvider, New.Common().Logger<EventDispatcher>().Creation);

                await dispatcher.Dispatch(new DualRoleEvent(), null);
            }

            recorder.EventHandledCount.Should().Be(1);
        }

        private IServiceCollection ScanForDualRoleHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(DualRoleHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddMessageHandlers(new Assembly[] { assembly });
            return sc;
        }

        private static int CountDescriptors(IServiceCollection services, Type serviceType, Type implementationType)
            => services.Count(sd => sd.ServiceType == serviceType && sd.ImplementationType == implementationType);

        public class HandlerInvocationRecorder
        {
            public int CommandHandledCount { get; private set; }
            public int EventHandledCount { get; private set; }
            public void RecordCommandHandled() => CommandHandledCount++;
            public void RecordEventHandled() => EventHandledCount++;
        }

        public class DualRoleCommand : ICommand { }
        public class DualRoleEvent : IEvent { }

        public class DualRoleHandler : IMessageHandler<DualRoleCommand>, IMessageHandler<DualRoleEvent>
        {
            private readonly HandlerInvocationRecorder _recorder;

            public DualRoleHandler(HandlerInvocationRecorder recorder)
                => _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));

            public Task Handle(DualRoleCommand message, IMessageHandlerContext context)
            {
                _recorder.RecordCommandHandled();
                return Task.CompletedTask;
            }

            public Task Handle(DualRoleEvent message, IMessageHandlerContext context)
            {
                _recorder.RecordEventHandled();
                return Task.CompletedTask;
            }
        }
    }
}
