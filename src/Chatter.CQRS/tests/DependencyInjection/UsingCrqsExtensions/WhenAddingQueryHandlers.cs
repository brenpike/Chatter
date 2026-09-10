using Chatter.CQRS.Context;
using Chatter.CQRS.Queries;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingQueryHandlers : Testing.Core.Context
    {
        [Fact]
        public void MustThrowIfDuplicateQueryHandlers()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeHandler), typeof(FakeHandler)).Creation;
            var sc = new ServiceCollection();
            Assert.ThrowsAny<Exception>(() => sc.AddQueryHandlers(new Assembly[] { assembly }));
        }

        [Fact]
        public void MustRegisterAllQueryHandlers()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddQueryHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeHandler));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IQueryHandler<FakeQuery, string>));
            sd.ImplementationType.Should().Be(typeof(FakeHandler));
        }

        [Fact]
        public void MustNotRegisterCollateralInterfacesImplementedByQueryHandlers()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCollateralHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddQueryHandlers(new Assembly[] { assembly });

            sc.Count(sd => sd.ServiceType == typeof(IFakeCollateralService) && sd.ImplementationType == typeof(FakeCollateralHandler)).Should().Be(0);
        }

        [Fact]
        public void MustPreserveAPreExistingRegistrationOfACollateralInterface()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCollateralHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddScoped<IFakeCollateralService, FakeCollateralService>();

            sc.AddQueryHandlers(new Assembly[] { assembly });

            var collateralDescriptors = sc.Where(sd => sd.ServiceType == typeof(IFakeCollateralService)).ToList();
            collateralDescriptors.Should().ContainSingle();
            collateralDescriptors[0].ImplementationType.Should().Be(typeof(FakeCollateralService));
            collateralDescriptors[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterNothingForAnOpenGenericQueryHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(OpenGenericFakeHandler<>)).Creation;
            var sc = new ServiceCollection();

            sc.AddQueryHandlers(new Assembly[] { assembly });

            sc.Should().BeEmpty();
        }

        [Fact]
        public void MustReturnSelf()
        {
            var sc = new ServiceCollection();
            var returnValue = sc.AddQueryHandlers(new Assembly[] { });
            returnValue.Should().BeSameAs(sc);
        }

        private interface IFakeCollateralService { }
        private class FakeCollateralService : IFakeCollateralService { }
        private class FakeCollateralQuery : IQuery<string> { }
        private class FakeCollateralHandler : IQueryHandler<FakeCollateralQuery, string>, IFakeCollateralService
        {
            public Task<string> Handle(FakeCollateralQuery query, IQueryHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeQuery : IQuery<string> { }
        private class OpenGenericFakeHandler<TUnconstrained> : IQueryHandler<FakeQuery, string>
        {
            public Task<string> Handle(FakeQuery query, IQueryHandlerContext context) => throw new NotImplementedException();
        }
        private class FakeHandler : IQueryHandler<FakeQuery, string>
        {
            public Task<string> Handle(FakeQuery query, IQueryHandlerContext context) => throw new NotImplementedException();
        }
    }
}
