using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingServiceCollectionExtensions
{
    public class WhenAddingPipelineBehaviorBranches
    {
        public WhenAddingPipelineBehaviorBranches() { }

        // L19: the `i.IsGenericType` predicate short-circuits to false for a non-generic
        // implemented interface, then a later ICommandBehavior<> interface matches. Existing
        // tests only exercise behavior types whose sole interface is ICommandBehavior<>, so the
        // false-arm of `i.IsGenericType` for a real interface is never evaluated until now.
        [Fact]
        public void MustRegisterWhenBehaviorAlsoImplementsNonGenericInterface()
        {
            var sc = new ServiceCollection();

            sc.AddPipelineBehavior(typeof(MarkedCommandBehavior<>));

            sc.Should().HaveCount(1);
            sc[0].ServiceType.Should().Be(typeof(ICommandBehavior<>));
            sc[0].ImplementationType.Should().Be(typeof(MarkedCommandBehavior<>));
        }

        // L94 (RegisterBehaviorForAllCommands): the `t.IsGenericType` predicate short-circuits to
        // false for the non-generic IMarker interface returned by GetInterfaces(), before the
        // matching ICommandBehavior<> interface is found. Existing tests use either a non-matching
        // type (throws) or a type whose only interface is ICommandBehavior<> (matches first arm).
        [Fact]
        public void MustRegisterForAllCommandsWhenOpenBehaviorAlsoImplementsNonGenericInterface()
        {
            var sc = new ServiceCollection();

            sc.RegisterBehaviorForAllCommands(typeof(MarkedCommandBehavior<>));

            sc.Should().HaveCount(1);
            sc[0].ServiceType.Should().Be(typeof(ICommandBehavior<>));
            sc[0].ImplementationType.Should().Be(typeof(MarkedCommandBehavior<>));
        }

        // L120 (RegisterBehaviorForCommand): same short-circuit-false arm of `t.IsGenericType`
        // for the closed-generic registration path, driven by the non-generic IMarker interface.
        [Fact]
        public void MustRegisterForCommandWhenClosedBehaviorAlsoImplementsNonGenericInterface()
        {
            var sc = new ServiceCollection();

            sc.RegisterBehaviorForCommand(typeof(MarkedCommandBehavior<FakeCommand>));

            sc.Should().HaveCount(1);
            sc[0].ServiceType.Should().Be(typeof(ICommandBehavior<FakeCommand>));
            sc[0].ImplementationType.Should().Be(typeof(MarkedCommandBehavior<FakeCommand>));
        }

        // L39-41 (GetServiceDescriptorsByImplementationType): a single collection mixing an
        // open-generic-implementation descriptor, a closed-generic-implementation descriptor, and a
        // non-generic-implementation descriptor, so BOTH arms of the OR are evaluated within one
        // Where pass. Existing tests place only one descriptor kind per collection, so a single
        // evaluation only ever exercises one arm.
        [Fact]
        public void MustMatchOnlyOpenGenericImplementationWhenQueryingOpenGenericType()
        {
            var sc = BuildMixedCollection();

            var foundServices = sc.GetServiceDescriptorsByImplementationType(typeof(MarkedCommandBehavior<>));

            foundServices.Should().HaveCount(1);
            foundServices.Single().ImplementationType.Should().Be(typeof(MarkedCommandBehavior<>));
        }

        [Fact]
        public void MustMatchOnlyNonGenericImplementationWhenQueryingNonGenericType()
        {
            var sc = BuildMixedCollection();

            var foundServices = sc.GetServiceDescriptorsByImplementationType(typeof(NonGenericService));

            foundServices.Should().HaveCount(1);
            foundServices.Single().ImplementationType.Should().Be(typeof(NonGenericService));
        }

        [Fact]
        public void MustMatchClosedGenericImplementationViaNonGenericArmWhenQueryingClosedGenericType()
        {
            var sc = BuildMixedCollection();

            // A closed generic (MarkedCommandBehavior<FakeCommand>) is NOT a generic type
            // definition, so it is matched by the second (non-generic-definition) arm via an
            // exact ImplementationType equality, not by the open-generic arm.
            var foundServices = sc.GetServiceDescriptorsByImplementationType(typeof(MarkedCommandBehavior<FakeCommand>));

            foundServices.Should().HaveCount(1);
            foundServices.Single().ImplementationType.Should().Be(typeof(MarkedCommandBehavior<FakeCommand>));
        }

        private static IServiceCollection BuildMixedCollection()
        {
            var sc = new ServiceCollection();
            sc.AddTransient(typeof(ICommandBehavior<>), typeof(MarkedCommandBehavior<>));
            sc.AddTransient(typeof(ICommandBehavior<FakeCommand>), typeof(MarkedCommandBehavior<FakeCommand>));
            sc.AddTransient(typeof(IMarker), typeof(NonGenericService));
            return sc;
        }

        private interface IMarker { }

        private class NonGenericService : IMarker { }

        private class FakeCommand : ICommand { }

        private class MarkedCommandBehavior<TMessage> : IMarker, ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
    }
}
