using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingChatterCqrs : Testing.Core.Context
    {
        [Fact]
        public void MustNotRegisterHandlersFromAssembliesThatWereNotSupplied()
        {
            var suppliedAssembly = New.Common().Assembly.WithTypes(typeof(SuppliedCommandHandler)).Creation;
            var services = new ServiceCollection();

            services.AddChatterCqrs(Mock.Of<IConfiguration>(), messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(suppliedAssembly));

            services.Should().NotContain(sd => sd.ImplementationType == typeof(UnsuppliedCommandHandler));
            services.Should().Contain(sd => sd.ServiceType == typeof(IMessageHandler<SuppliedCommand>)
                                            && sd.ImplementationType == typeof(SuppliedCommandHandler));
        }

        [Fact]
        public void MustRegisterHandlersFromLoadedAssembliesWhenNoAssembliesAreSupplied()
        {
            var services = new ServiceCollection();

            services.AddChatterCqrs(Mock.Of<IConfiguration>());

            services.Should().Contain(sd => sd.ServiceType == typeof(IMessageHandler<UnsuppliedCommand>)
                                            && sd.ImplementationType == typeof(UnsuppliedCommandHandler));
        }

        [Fact]
        public void MustReadTheAssemblySourceOnlyOnceForEveryRegistrationScan()
        {
            var sourceProvider = new GrowingAssemblySourceProvider(
                New.Common().Assembly.WithFullName("Chatter.Fake.Registered").WithTypes(typeof(SuppliedCommandHandler)).Creation,
                New.Common().Assembly.WithFullName("Chatter.Fake.Late").WithTypes(typeof(LateEventHandler)).Creation);

            new ServiceCollection().AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                   messageHandlerSourceBuilder: b => b.WithSourceProvider(sourceProvider)
                                                                                      .WithNamespaceSelector("Chatter.Fake*"));

            sourceProvider.ReadCount.Should().Be(1);
        }

        [Fact]
        public void MustRegisterHandlersFromOneAssemblySetEvenWhenTheSourceGrowsBetweenScans()
        {
            var registeredAssembly = New.Common().Assembly.WithFullName("Chatter.Fake.Registered").WithTypes(typeof(SuppliedCommandHandler)).Creation;
            var lateAssembly = New.Common().Assembly.WithFullName("Chatter.Fake.Late").WithTypes(typeof(LateEventHandler)).Creation;
            var services = new ServiceCollection();

            services.AddChatterCqrs(Mock.Of<IConfiguration>(),
                                    messageHandlerSourceBuilder: b => b.WithSourceProvider(new GrowingAssemblySourceProvider(registeredAssembly, lateAssembly))
                                                                       .WithNamespaceSelector("Chatter.Fake*"));

            services.Should().Contain(sd => sd.ImplementationType == typeof(SuppliedCommandHandler));
            services.Should().NotContain(sd => sd.ImplementationType == typeof(LateEventHandler));
        }

        [Fact]
        public void MustRecordTheHandlerScanOnceHoweverManyTimesChatterCqrsIsAdded()
        {
            var firstAssembly = New.Common().Assembly.WithFullName("Chatter.Fake.First").WithTypes(typeof(SuppliedCommandHandler)).Creation;
            var secondAssembly = New.Common().Assembly.WithFullName("Chatter.Fake.Second").WithTypes(typeof(LateEventHandler)).Creation;
            var services = new ServiceCollection();

            services.AddChatterCqrs(Mock.Of<IConfiguration>(), messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(firstAssembly));
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(firstAssembly, secondAssembly));

            services.Should().ContainSingle(sd => sd.ServiceType == typeof(HandlerScanRecord))
                    .Which.ImplementationInstance.Should().BeOfType<HandlerScanRecord>()
                    .Which.ScannedAssemblies.Should().HaveCount(2).And.Contain(firstAssembly).And.Contain(secondAssembly);
        }

        /// <summary>
        /// A source whose returned sequence is re-evaluated on every enumeration and yields the late assembly from its
        /// second read onwards, as a provider over a live assembly list would.
        /// </summary>
        private class GrowingAssemblySourceProvider : IAssemblyFilterSourceProvider
        {
            private readonly Assembly _registeredAssembly;
            private readonly Assembly _lateAssembly;

            public GrowingAssemblySourceProvider(Assembly registeredAssembly, Assembly lateAssembly)
            {
                _registeredAssembly = registeredAssembly;
                _lateAssembly = lateAssembly;
            }

            public int ReadCount { get; private set; }

            public IEnumerable<Assembly> GetSourceAssemblies() => ReadSourceAssemblies();

            private IEnumerable<Assembly> ReadSourceAssemblies()
            {
                ReadCount++;
                yield return _registeredAssembly;

                if (ReadCount > 1)
                {
                    yield return _lateAssembly;
                }
            }
        }

        private class LateEvent : IEvent { }
        private class LateEventHandler : IMessageHandler<LateEvent>
        {
            public Task Handle(LateEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class SuppliedCommand : ICommand { }
        private class SuppliedCommandHandler : IMessageHandler<SuppliedCommand>
        {
            public Task Handle(SuppliedCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class UnsuppliedCommand : ICommand { }
        private class UnsuppliedCommandHandler : IMessageHandler<UnsuppliedCommand>
        {
            public Task Handle(UnsuppliedCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }
    }
}
