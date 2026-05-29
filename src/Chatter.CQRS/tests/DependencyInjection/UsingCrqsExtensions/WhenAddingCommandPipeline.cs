using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingCommandPipeline
    {
        private static IChatterBuilder CreateChatterBuilder(IServiceCollection services)
            => ChatterBuilder.Create(
                services,
                new Mock<IConfiguration>().Object,
                new Mock<IAssemblySourceFilter>().Object);

        [Fact]
        public void MustRegisterOpenGenericCommandPipelineAndRunSuppliedAction()
        {
            // AddCommandPipeline(IChatterBuilder, Action<CommandPipelineBuilder>) — CqrsExtensions.cs L91-105.
            var services = new ServiceCollection();
            var chatterBuilder = CreateChatterBuilder(services);
            Action<CommandPipelineBuilder> pipelineBuilder = b => b.WithBehavior<FakeCommandBehavior<FakeCommand>>();

            // Explicit static call disambiguates from the [ExcludeFromCodeCoverage] ObsoleteCqrsExtensions shim,
            // pinning the internal CqrsExtensions.AddCommandPipeline (L91-105) under test.
            var returned = CqrsExtensions.AddCommandPipeline(chatterBuilder, pipelineBuilder);

            returned.Should().BeSameAs(chatterBuilder);

            var pipeline = services.GetServiceDescriptorByImplementationType(typeof(CommandBehaviorPipeline<>));
            pipeline.Should().NotBeNull();
            pipeline.ServiceType.Should().Be(typeof(ICommandBehaviorPipeline<>));
            pipeline.ImplementationType.Should().Be(typeof(CommandBehaviorPipeline<>));
            pipeline.Lifetime.Should().Be(ServiceLifetime.Transient);

            // The supplied action actually ran: WithBehavior registered the closed command behavior.
            var behavior = services.GetServiceDescriptorByImplementationType(typeof(FakeCommandBehavior<FakeCommand>));
            behavior.Should().NotBeNull();
            behavior.ServiceType.Should().Be(typeof(ICommandBehavior<FakeCommand>));
        }

        [Fact]
        public void MustRegisterOpenGenericCommandPipelineWhenActionIsNull()
        {
            // AddCommandPipeline with null pipelineBuilder — CqrsExtensions.cs L102 (?.Invoke).
            var services = new ServiceCollection();
            var chatterBuilder = CreateChatterBuilder(services);
            Action<CommandPipelineBuilder> pipelineBuilder = null;

            // Explicit static call disambiguates from the [ExcludeFromCodeCoverage] ObsoleteCqrsExtensions shim,
            // pinning the internal CqrsExtensions.AddCommandPipeline (L91-105) under test.
            IChatterBuilder returned = null;
            Action act = () => returned = CqrsExtensions.AddCommandPipeline(chatterBuilder, pipelineBuilder);

            act.Should().NotThrow();
            returned.Should().BeSameAs(chatterBuilder);

            var pipeline = services.GetServiceDescriptorByImplementationType(typeof(CommandBehaviorPipeline<>));
            pipeline.Should().NotBeNull();
            pipeline.ServiceType.Should().Be(typeof(ICommandBehaviorPipeline<>));
            pipeline.ImplementationType.Should().Be(typeof(CommandBehaviorPipeline<>));
            pipeline.Lifetime.Should().Be(ServiceLifetime.Transient);
        }

        private class FakeCommand : ICommand { }
        private class FakeCommandBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
    }
}
