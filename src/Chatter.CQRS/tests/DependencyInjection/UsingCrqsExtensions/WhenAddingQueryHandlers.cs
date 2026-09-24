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
        public void MustThrowWhenTwoScannedHandlersHandleTheSameQuery()
        {
            // INVARIANT: the competing handler is scanned in its closed form so the open definition the test assembly
            // itself carries is dropped by IsClosedHandlerType; a non-generic second FakeQuery handler makes every
            // zero-arg AddChatterCqrs scan of this assembly throw under RegistrationStrategy.Throw. Oracle:
            // WhenAddingChatterCqrs.MustRegisterHandlersFromLoadedAssembliesWhenNoAssembliesAreSupplied, observed red
            // with DuplicateTypeRegistrationException when FakeCompetingHandler was non-generic.
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeHandler), typeof(FakeCompetingHandler<int>)).Creation;
            var sc = new ServiceCollection();

            FluentActions.Invoking(() => sc.AddQueryHandlers(new Assembly[] { assembly })).Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void MustNotThrowForOneQueryHandlerReachableFromTwoScannedAssemblies()
        {
            // INVARIANT: characterization pin on Scrutor 7's type dedup. TypeSourceSelector.AddSelector collects the
            // scanned types with types.ToHashSet(), so one handler type reachable from two scanned assemblies registers
            // once and RegistrationStrategy.Throw sees no duplicate; under Scrutor 3.3.0 this scan threw. Oracle: this
            // fact. Observed red by scanning each assembly in its own services.Scan call in
            // CqrsExtensions.AddQueryHandlers (each selector dedups only its own types, so the second scan's Throw
            // fires). A Scrutor version without type dedup also reddens it.
            var first = New.Common().Assembly.WithTypes(typeof(FakeHandler)).Creation;
            var second = New.Common().Assembly.WithTypes(typeof(FakeHandler)).Creation;
            var sc = new ServiceCollection();

            FluentActions.Invoking(() => sc.AddQueryHandlers(new[] { first, second })).Should().NotThrow();

            sc.Count(sd => sd.ServiceType == typeof(IQueryHandler<FakeQuery, string>)).Should().Be(1);
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
        public void MustRegisterNothingForAnArityMismatchedOpenGenericQueryHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(OpenGenericFakeHandler<>)).Creation;
            var sc = new ServiceCollection();

            sc.AddQueryHandlers(new Assembly[] { assembly });

            sc.Should().BeEmpty();
        }

        [Fact]
        public void MustRegisterNothingForAnArityMatchedOpenGenericQueryHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(ArityMatchedFakeHandler<,>)).Creation;
            var sc = new ServiceCollection();

            sc.AddQueryHandlers(new Assembly[] { assembly });

            sc.Should().BeEmpty();

            using (var serviceProvider = sc.BuildServiceProvider())
            {
                serviceProvider.GetService(typeof(IQueryHandler<ArityMatchedProbeQuery, string>)).Should().BeNull();
            }
        }

        [Fact]
        public void MustRegisterOnlyClosedServiceTypesWhenScanningAMixOfQueryHandlerShapes()
        {
            var assembly = New.Common().Assembly
                .WithTypes(typeof(MixedProbeHandler), typeof(ArityMatchedFakeHandler<,>), typeof(OpenGenericFakeHandler<>))
                .Creation;
            var sc = new ServiceCollection();

            sc.AddQueryHandlers(new Assembly[] { assembly });

            sc.Should().OnlyContain(sd => sd.ServiceType.ContainsGenericParameters == false);
            sc.Count(sd => sd.ServiceType == typeof(IQueryHandler<MixedProbeQuery, string>) && sd.ImplementationType == typeof(MixedProbeHandler)).Should().Be(1);

            using (var serviceProvider = sc.BuildServiceProvider())
            {
                serviceProvider.GetService(typeof(IQueryHandler<MixedProbeQuery, string>)).Should().BeOfType<MixedProbeHandler>();
            }
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
        public class MixedProbeQuery : IQuery<string> { }
        public class MixedProbeHandler : IQueryHandler<MixedProbeQuery, string>
        {
            public Task<string> Handle(MixedProbeQuery query, IQueryHandlerContext context) => throw new NotImplementedException();
        }

        public class ArityMatchedProbeQuery : IQuery<string> { }
        public class ArityMatchedFakeHandler<TQuery, TResult> : IQueryHandler<TQuery, TResult> where TQuery : class, IQuery<TResult>
        {
            public Task<TResult> Handle(TQuery query, IQueryHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeHandler : IQueryHandler<FakeQuery, string>
        {
            public Task<string> Handle(FakeQuery query, IQueryHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeCompetingHandler<TUnused> : IQueryHandler<FakeQuery, string>
        {
            public Task<string> Handle(FakeQuery query, IQueryHandlerContext context) => throw new NotImplementedException();
        }
    }
}
