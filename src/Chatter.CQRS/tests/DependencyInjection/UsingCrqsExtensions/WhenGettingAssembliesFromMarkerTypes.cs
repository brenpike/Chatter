using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenGettingAssembliesFromMarkerTypes
    {
        public WhenGettingAssembliesFromMarkerTypes()
        { }

        [Fact]
        public void MustReturnUnionOfMarkerTypesAndCurrentAppDomainAssemblies()
        {
            var mockAssembly = new Mock<Assembly>();
            var fakeType1 = new Mock<Type>();
            fakeType1.SetupGet(a => a.Assembly).Returns(mockAssembly.Object);
            mockAssembly.Setup(a => a.FullName).Returns("fake.assembly.1");
            mockAssembly.Setup(a => a.GetTypes()).Returns(new Type[] { fakeType1.Object });

            var currentAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var assemblies = new Assembly[] { mockAssembly.Object };
            var currentAssembliesUnionMarkerTypesExpected = assemblies.Union(currentAssemblies);

            var currentAssembliesUnionMarkerTypesActual = CqrsExtensions.GetAssembliesFromMarkerTypes(new Type[] { fakeType1.Object });
            currentAssembliesUnionMarkerTypesActual.Should().HaveCount(currentAssembliesUnionMarkerTypesExpected.Count());
            currentAssembliesUnionMarkerTypesActual.Should().BeEquivalentTo(currentAssembliesUnionMarkerTypesExpected);
            currentAssembliesUnionMarkerTypesActual.Should().NotBeEmpty();
            currentAssemblies.Should().BeSubsetOf(currentAssembliesUnionMarkerTypesActual);
        }
    }
}
