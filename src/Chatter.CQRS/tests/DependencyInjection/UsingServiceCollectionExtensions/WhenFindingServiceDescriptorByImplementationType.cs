using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingServiceCollectionExtensions
{
    public class WhenFindingServiceDescriptorByImplementationType
    {
        public WhenFindingServiceDescriptorByImplementationType() { }

        [Fact]
        public void MustReturnServiceDescriptorIfImplementationTypeIsNotNullAndMatchesSearchType()
        {
            var sc = new ServiceCollection();

            var sd1 = new ServiceDescriptor(typeof(IEvent), typeof(NotACommand), ServiceLifetime.Transient);
            var sd2 = new ServiceDescriptor(typeof(ICommand), typeof(FakeCommand), ServiceLifetime.Transient);
            var sd3 = new ServiceDescriptor(typeof(IMessage), typeof(FakeCommand), ServiceLifetime.Transient);

            sc.AddTransient(typeof(IEvent), typeof(NotACommand));
            sc.AddTransient(typeof(ICommand), typeof(FakeCommand));
            sc.AddTransient(typeof(IMessage), typeof(FakeCommand));

            var foundServices = sc.GetServiceDescriptorsByImplementationType(typeof(FakeCommand));

            foundServices.Should().HaveCount(2);
            Assert.All(foundServices, x =>
            {
                Assert.Equal(ServiceLifetime.Transient, x.Lifetime);
                Assert.Equal(typeof(FakeCommand), x.ImplementationType);
            });
        }

        [Fact]
        public void MustReturnServiceDescriptorIfImplementationTypeIsNotNullAndMatchesGenericSearchType()
        {
            var sc = new ServiceCollection();

            var sd1 = new ServiceDescriptor(typeof(IEvent), typeof(NotACommand), ServiceLifetime.Transient);
            var sd2 = new ServiceDescriptor(typeof(ICommand), typeof(ICommandBehavior<ICommand>), ServiceLifetime.Transient);
            var sd3 = new ServiceDescriptor(typeof(IMessage), typeof(ICommandBehavior<>), ServiceLifetime.Transient);

            sc.AddTransient(typeof(IEvent), typeof(NotACommand));
            sc.AddTransient(typeof(ICommand), typeof(ICommandBehavior<>));
            sc.AddTransient(typeof(IMessage), typeof(ICommandBehavior<>));

            var foundServices = sc.GetServiceDescriptorsByImplementationType(typeof(ICommandBehavior<>));

            foundServices.Should().HaveCount(2);
            Assert.All(foundServices, x =>
            {
                Assert.Equal(ServiceLifetime.Transient, x.Lifetime);
                Assert.Equal(typeof(ICommandBehavior<>), x.ImplementationType);
            });
        }

        private class NotACommand { }
        private class FakeCommand : ICommand { }
        private class AnotherFakeCommand : ICommand { }
    }
}
