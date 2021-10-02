using Chatter.CQRS.DependencyInjection;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.CQRS;
using FluentAssertions;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceFilter
{
    public class WhenApplyingFilter : Testing.Core.Context
    {
        [Fact]
        public void MustContainExplicitAssemblies()
        {
            var assembly = New.Common().Assembly.Creation;
            var explicitAssemblies = new Assembly[] { assembly };

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, string.Empty, explicitAssemblies);
            var result = filter.Apply();

            Assert.Equal(explicitAssemblies, filter.ExplictAssemblies);
            Assert.Contains(assembly, result);
        }

        [Fact]
        public void MustNotContainDuplicateAssemblies()
        {
            var assembly = New.Common().Assembly.Creation;
            var explicitAssemblies = new Assembly[] { assembly };

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider.WithSourceAssemblies(assembly).Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, string.Empty, explicitAssemblies);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            Assert.Contains(assembly, result);
        }

        [Fact]
        public void MustNotFilterAnyAssembliesWhenNameselectorIsNullAndNoExplicitAssemblies()
        {
            var assembly = New.Common().Assembly.Creation;
            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, null, null);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            result.Should().Contain(assembly);
        }

        [Fact]
        public void MustNotFilterAnyAssembliesWhenNameselectorIsNullWithExplicitAssemblies()
        {
            var assembly = New.Common().Assembly.Creation;
            var assembly2 = New.Common().Assembly.Creation;
            var explicitAssemblies = new Assembly[] { assembly };

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly2)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, null, explicitAssemblies);
            var result = filter.Apply();

            result.Should().HaveCount(2);
            result.Should().Contain(assembly);
            result.Should().Contain(assembly2);
        }

        [Fact]
        public void MustNotFilterAnyAssembliesWhenNamespaceSelectorIsEmptyAndNoExplicitAssemblies()
        {
            var assembly = New.Common().Assembly.Creation;
            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, string.Empty, null);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            result.Should().Contain(assembly);
        }

        [Fact]
        public void MustNotFilterAnyAssembliesWhenNamespaceSelectorIsEmptyWithExplicitAssemblies()
        {
            var assembly = New.Common().Assembly.Creation;
            var assembly2 = New.Common().Assembly.Creation;
            var explicitAssemblies = new Assembly[] { assembly };

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly2)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, string.Empty, explicitAssemblies);
            var result = filter.Apply();

            result.Should().HaveCount(2);
            result.Should().Contain(assembly);
            result.Should().Contain(assembly2);
        }

        [Fact]
        public void MustIgnoreCaseWhenApplyingNamespaceSelectorFilterToAssemblyFullName()
        {
            var fullName = "Chatter.Cqrs";
            var assembly = New.Common().Assembly
                .WithFullName(fullName)
                .Creation;

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, fullName.ToUpper(), null);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            result.First().FullName.Should().Be(fullName);
            result.First().Should().BeSameAs(assembly);
        }

        [Fact]
        public void MustIgnoreCaseWhenApplyingNamespaceSelectorFilterToAssemblyTypeNamespaces()
        {
            var @namespace = "This.is.a.Namespace";
            var type = New.Common().Type
                .WithNamespace(@namespace)
                .Creation;
            var assembly = New.Common().Assembly
                .WithTypes(type)
                .Creation;

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, @namespace.ToUpper(), null);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            result.First().Should().BeSameAs(assembly);
        }

        [Fact]
        public void MustMatchNamespaceSelectorWithWildcardsToAssemblyFullName()
        {
            var fullName = "Chatter.Cqrs";
            var assembly = New.Common().Assembly
                .WithFullName(fullName)
                .Creation;

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, "Chatter.*", null);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            result.First().FullName.Should().Be(fullName);
            result.First().Should().BeSameAs(assembly);
        }

        [Fact]
        public void MustMatchNamespaceSelectorWithWildcardsToAssemblyTypeNamespaces()
        {
            var @namespace = "This.is.a.Namespace";
            var type = New.Common().Type
                .WithNamespace(@namespace)
                .Creation;
            var assembly = New.Common().Assembly
                .WithTypes(type)
                .Creation;

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, "This*", null);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            result.First().Should().BeSameAs(assembly);
        }

        [Fact]
        public void MustMatchNamespaceSelectorWithSingleSymbolWildcardToAssemblyFullName()
        {
            var fullName = "Chatter.Cqrs";
            var assembly = New.Common().Assembly
                .WithFullName(fullName)
                .Creation;

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, "Chatter?Cqrs", null);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            result.First().FullName.Should().Be(fullName);
            result.First().Should().BeSameAs(assembly);
        }

        [Fact]
        public void MustMatchNamespaceSelectorWithSingleSymbolWildcardToAssemblyTypeNamespaces()
        {
            var @namespace = "This.is.a.Namespace";
            var type = New.Common().Type
                .WithNamespace(@namespace)
                .Creation;
            var assembly = New.Common().Assembly
                .WithTypes(type)
                .Creation;

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, "Th?s.is.a?Namesp?ce", null);
            var result = filter.Apply();

            result.Should().HaveCount(1);
            result.First().Should().BeSameAs(assembly);
        }

        [Fact]
        public void MustNotContainAssemblyThatDoesntMatchNamespaceSelector()
        {
            var @namespace = "This.is.a.Namespace";
            var type = New.Common().Type
                .WithNamespace(@namespace)
                .Creation;
            var assembly = New.Common().Assembly
                .WithFullName(@namespace)
                .WithTypes(type)
                .Creation;

            var assemblyFilterSourceProvider = New.Cqrs().AssemblyFilterSourceProvider
                .WithSourceAssemblies(assembly)
                .Creation;

            var filter = new AssemblySourceFilter(assemblyFilterSourceProvider, "non-matching namespace filter", null);
            var result = filter.Apply();

            result.Should().BeEmpty();
        }
    }
}
