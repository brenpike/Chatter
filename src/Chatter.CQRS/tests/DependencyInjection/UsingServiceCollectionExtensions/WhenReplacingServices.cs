using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingServiceCollectionExtensions
{
    public class WhenReplacingServices
    {
        public WhenReplacingServices() { }

        [Fact]
        public void MustReplaceServiceDescriptorForSpecifiedFactoryIfNotAlreadyRegistered()
        {
            var sc = new ServiceCollection();
            Func<IServiceProvider, TransientClass> factory = sp => new TransientClass();
            Func<IServiceProvider, TransientClass2> factory2 = sp => new TransientClass2();

            sc.AddScoped<ITransientInterface>(factory2);

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().NotBe(ServiceLifetime.Transient);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
            sc[0].ImplementationFactory.Should().NotBeSameAs(factory);
            sc[0].ImplementationFactory.Should().BeSameAs(factory2);
            sc[0].ImplementationType.Should().BeNull();
            sc[0].ServiceType.Should().Be(typeof(ITransientInterface));

            sc.Replace<ITransientInterface>(ServiceLifetime.Transient, factory);

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Transient);
            sc[0].Lifetime.Should().NotBe(ServiceLifetime.Scoped);
            sc[0].ImplementationFactory.Should().BeSameAs(factory);
            sc[0].ImplementationFactory.Should().NotBeSameAs(factory2);
            sc[0].ImplementationType.Should().BeNull();
            sc[0].ServiceType.Should().Be(typeof(ITransientInterface));
        }

        [Fact]
        public void MustReplaceServiceDescriptorForImplementationTypeIfNotAlreadyRegistered()
        {
            var sc = new ServiceCollection();
            sc.AddScoped<ITransientInterface, TransientClass>();

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().NotBe(ServiceLifetime.Transient);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
            sc[0].ImplementationFactory.Should().BeNull();
            sc[0].ImplementationType.Should().Be(typeof(TransientClass));
            sc[0].ImplementationType.Should().NotBe(typeof(TransientClass2));
            sc[0].ServiceType.Should().Be(typeof(ITransientInterface));

            sc.Replace<ITransientInterface, TransientClass2>(ServiceLifetime.Transient);

            sc.Should().HaveCount(1);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Transient);
            sc[0].Lifetime.Should().NotBe(ServiceLifetime.Scoped);
            sc[0].ImplementationFactory.Should().BeNull();
            sc[0].ImplementationType.Should().NotBe(typeof(TransientClass));
            sc[0].ImplementationType.Should().Be(typeof(TransientClass2));
            sc[0].ServiceType.Should().Be(typeof(ITransientInterface));
        }

        private class TransientClass : ITransientInterface { }
        private class TransientClass2 : ITransientInterface { }
        private interface ITransientInterface { }
    }
}
