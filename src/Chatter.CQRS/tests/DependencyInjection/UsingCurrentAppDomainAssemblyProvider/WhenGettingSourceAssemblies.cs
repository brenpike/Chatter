using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using System.Linq;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceProvider
{
    public class WhenGettingSourceAssemblies
    {
        [Fact]
        public void MustReturnCurrentAppDomainAssemblies()
        {
            var actual = CurrentAppDomainAssemblyProvider.Default.GetSourceAssemblies().ToList();
            actual.Should().NotBeEmpty();
            actual.Should().Contain(typeof(CurrentAppDomainAssemblyProvider).Assembly);
            actual.Should().Contain(typeof(WhenGettingSourceAssemblies).Assembly);
        }
    }
}
