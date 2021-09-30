using Chatter.CQRS.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingInMemoryQueryDispatcher
    {
        [Fact]
        public void MustAddScopedQueryDispatcherToServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.AddInMemoryQueryDispatcher();

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(QueryDispatcher));

            sd.Lifetime.Should().Be(ServiceLifetime.Scoped);
            sd.ServiceType.Should().Be(typeof(IQueryDispatcher));
            sd.ImplementationType.Should().Be(typeof(QueryDispatcher));
        }

        [Fact]
        public void MustReturnSelf()
        {
            var sc = new ServiceCollection();
            var returnValue = sc.AddInMemoryQueryDispatcher();
            returnValue.Should().BeSameAs(sc);
        }
    }
}
