using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using System;
using System.Linq;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceProvider
{
    public class WhenGettingSourceAssemblies
    {
        [Fact]
        public void MustReturnCurrentAppDomainAssemblies()
        {
            // Snapshot the AppDomain assemblies first; the provider is expected to return at
            // least this snapshot. Asserting a superset (rather than exact-set equality) keeps
            // the test order-independent and tolerant of assemblies loaded between the two
            // calls, while still failing if the provider drops currently-loaded assemblies.
            var snapshot = AppDomain.CurrentDomain.GetAssemblies();

            var actual = CurrentAppDomainAssemblyProvider.Default.GetSourceAssemblies().ToList();

            actual.Should().NotBeEmpty();
            actual.Should().Contain(snapshot);
            actual.Should().Contain(typeof(CurrentAppDomainAssemblyProvider).Assembly);
            actual.Should().Contain(typeof(WhenGettingSourceAssemblies).Assembly);
        }
    }
}
