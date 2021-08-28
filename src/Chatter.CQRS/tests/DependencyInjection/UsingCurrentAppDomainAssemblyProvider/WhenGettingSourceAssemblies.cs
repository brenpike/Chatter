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
            var currentAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var sut = CurrentAppDomainAssemblyProvider.Default;
            var currentAssembliesUnionMarkerTypesActual = sut.GetSourceAssemblies();
            currentAssembliesUnionMarkerTypesActual.Should().HaveCount(currentAssemblies.Count());
            currentAssembliesUnionMarkerTypesActual.Should().BeEquivalentTo(currentAssemblies);
            currentAssembliesUnionMarkerTypesActual.Should().NotBeEmpty();
        }
    }
}
