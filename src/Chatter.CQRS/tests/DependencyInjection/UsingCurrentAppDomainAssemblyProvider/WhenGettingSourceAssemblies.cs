using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
            // Dynamic assemblies are excluded from the snapshot because the provider intentionally
            // filters them out (see CurrentAppDomainAssemblyProvider.GetSourceAssemblies).
            var snapshot = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).ToArray();

            var actual = CurrentAppDomainAssemblyProvider.Default.GetSourceAssemblies().ToList();

            actual.Should().NotBeEmpty();
            actual.Should().Contain(snapshot);
            actual.Should().Contain(typeof(CurrentAppDomainAssemblyProvider).Assembly);
            actual.Should().Contain(typeof(WhenGettingSourceAssemblies).Assembly);
        }

        [Fact]
        public void MustExcludeDynamicAssemblies()
        {
            // Emit a dynamic assembly so it is present in the current AppDomain. Why the provider
            // excludes it: see the INVARIANT on CurrentAppDomainAssemblyProvider.GetSourceAssemblies.
            var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"Chatter.Tests.Dynamic.{Guid.NewGuid():N}"),
                AssemblyBuilderAccess.Run);
            dynamicAssembly.IsDynamic.Should().BeTrue();

            var actual = CurrentAppDomainAssemblyProvider.Default.GetSourceAssemblies().ToList();

            actual.Should().NotContain(assembly => assembly.IsDynamic);
            actual.Should().NotContain(dynamicAssembly);
            actual.Should().Contain(typeof(CurrentAppDomainAssemblyProvider).Assembly);
        }
    }
}
