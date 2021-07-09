using Chatter.CQRS.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingInMemoryQueryDispatcher
    {
        [Fact]
        public void MustAddScopedQueryDispatcher()
        {
            var sc = new ServiceCollection();
            sc.AddInMemoryQueryDispatcher();

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
            sc[0].ServiceType.Should().Be(typeof(IQueryDispatcher));
            sc[0].ImplementationType.Should().Be(typeof(QueryDispatcher));
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
