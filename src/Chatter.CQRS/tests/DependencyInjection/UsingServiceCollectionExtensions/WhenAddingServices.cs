using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingServiceCollectionExtensions
{
    public class WhenAddingServices
    {
        public WhenAddingServices() { }

        [Fact]
        public void MustAddServiceDescriptorForSpecifiedFactoryIfNotAlreadyRegistered()
        {
            var sc = new ServiceCollection();
            Func<IServiceProvider, TransientClass> factory = sp => new TransientClass();

            sc.AddIfNotRegistered(ServiceLifetime.Transient, factory);

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Transient);
            sc[0].ImplementationFactory.Should().BeSameAs(factory);
            sc[0].ImplementationType.Should().BeNull();
            sc[0].ServiceType.Should().Be(typeof(TransientClass));
        }

        [Fact]
        public void MustAddServiceDescriptorForImplementationTypeIfNotAlreadyRegistered()
        {
            var sc = new ServiceCollection();
            sc.AddIfNotRegistered<ITransientInterface, TransientClass>(ServiceLifetime.Transient);

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Transient);
            sc[0].ImplementationFactory.Should().BeNull();
            sc[0].ImplementationType.Should().Be(typeof(TransientClass));
            sc[0].ServiceType.Should().Be(typeof(ITransientInterface));
        }

        [Fact]
        public void MustNotAddServiceDescriptorForSpecifiedFactoryIfNotAlreadyRegistered()
        {
            var sc = new ServiceCollection();
            Func<IServiceProvider, TransientClass> factory = sp => new TransientClass();
            Func<IServiceProvider, TransientClass2> factory2 = sp => new TransientClass2();

            sc.AddIfNotRegistered<ITransientInterface>(ServiceLifetime.Transient, factory);
            sc.AddIfNotRegistered<ITransientInterface>(ServiceLifetime.Scoped, factory2);

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Transient);
            sc[0].Lifetime.Should().NotBe(ServiceLifetime.Scoped);
            sc[0].ImplementationFactory.Should().BeSameAs(factory);
            sc[0].ImplementationFactory.Should().NotBeSameAs(factory2);
            sc[0].ImplementationType.Should().BeNull();
            sc[0].ServiceType.Should().Be(typeof(ITransientInterface));
        }

        [Fact]
        public void MustNotAddServiceDescriptorForImplementationTypeIfNotAlreadyRegistered()
        {
            var sc = new ServiceCollection();
            sc.AddIfNotRegistered<ITransientInterface, TransientClass>(ServiceLifetime.Transient);
            sc.AddIfNotRegistered<ITransientInterface, TransientClass2>(ServiceLifetime.Scoped);

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Transient);
            sc[0].Lifetime.Should().NotBe(ServiceLifetime.Scoped);
            sc[0].ImplementationFactory.Should().BeNull();
            sc[0].ImplementationType.Should().Be(typeof(TransientClass));
            sc[0].ServiceType.Should().Be(typeof(ITransientInterface));
            sc[0].ImplementationType.Should().NotBe(typeof(TransientClass2));
        }

        private class TransientClass : ITransientInterface { }
        private class TransientClass2 : ITransientInterface { }
        private interface ITransientInterface { }
    }
}
