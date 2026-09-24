using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingServiceCollectionExtensions
{
    public class WhenRegisteringCommandBehaviorForAllCommands
    {
        public WhenRegisteringCommandBehaviorForAllCommands() { }

        [Fact]
        public void MustThrowWhenClosedGenericCommandBehaviorIsSupplied()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.RegisterBehaviorForAllCommands(typeof(FakeCommandBehavior<FakeCommand>))).Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void MustThrowWhenTypeIsNotICommandBehavior()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.RegisterBehaviorForAllCommands(typeof(FakeCommandBehavior3<>))).Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void MustThrowWhenTypeIsNull()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.RegisterBehaviorForAllCommands(null)).Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void MustRegisterOpenCommandBehaviors()
        {
            var sc = new ServiceCollection();
            sc.RegisterBehaviorForAllCommands(typeof(FakeCommandBehavior<>));
            sc.Should().HaveCount(1);
            sc[0].ServiceType.Should().Be(typeof(ICommandBehavior<>));
            sc[0].ImplementationType.Should().Be(typeof(FakeCommandBehavior<>));

            var sp = sc.BuildServiceProvider();
            var cb1 = sp.GetServices<ICommandBehavior<FakeCommand>>();
            var cb2 = sp.GetServices<ICommandBehavior<AnotherFakeCommand>>();

            cb1.Should().HaveCount(1);
            cb1.Should().ContainItemsAssignableTo<FakeCommandBehavior<FakeCommand>>();
            cb2.Should().HaveCount(1);
            cb2.Should().ContainItemsAssignableTo<FakeCommandBehavior<AnotherFakeCommand>>();
        }

        [Fact]
        public void MustRegisterANonPublicOpenGenericBehavior()
        {
            var sc = new ServiceCollection();

            sc.RegisterBehaviorForAllCommands(typeof(FakeCommandBehavior<>));

            typeof(FakeCommandBehavior<>).IsVisible.Should().BeFalse();
            sc.Should().ContainSingle(d => d.ServiceType == typeof(ICommandBehavior<>) && d.ImplementationType == typeof(FakeCommandBehavior<>));
        }

        [Fact]
        public void MustReplaceRegistrationsThatHaveSameImplementationTypeWithTransientLifetimeScope()
        {
            var sc = new ServiceCollection();
            sc.AddScoped(typeof(ICommandBehavior<>), typeof(FakeCommandBehavior<>));

            sc.Should().HaveCount(1);
            sc[0].ServiceType.Should().Be(typeof(ICommandBehavior<>));
            sc[0].ImplementationType.Should().Be(typeof(FakeCommandBehavior<>));
            sc[0].Lifetime.Should().Be(ServiceLifetime.Scoped);

            sc.RegisterBehaviorForAllCommands(typeof(FakeCommandBehavior<>));

            sc.Should().HaveCount(1);

            Assert.All(sc, x =>
            {
                Assert.Equal(ServiceLifetime.Transient, x.Lifetime);
                Assert.Equal(typeof(FakeCommandBehavior<>), x.ImplementationType);
                Assert.Equal(typeof(ICommandBehavior<>), x.ServiceType);
            });

            var sp = sc.BuildServiceProvider();
            var cb1 = sp.GetServices<ICommandBehavior<FakeCommand>>();
            var cb2 = sp.GetServices<ICommandBehavior<AnotherFakeCommand>>();

            cb1.Should().HaveCount(1);
            cb1.Should().ContainItemsAssignableTo<FakeCommandBehavior<FakeCommand>>();
            cb2.Should().HaveCount(1);
            cb2.Should().ContainItemsAssignableTo<FakeCommandBehavior<AnotherFakeCommand>>();
        }

        [Fact]
        public void MustKeepTheCommandBehaviorRegistrationWhenAGenericCollateralInterfaceIsDeclaredAfterIt()
        {
            var sc = new ServiceCollection();

            sc.RegisterBehaviorForAllCommands(typeof(CollateralAfterBehavior<>));

            var sp = sc.BuildServiceProvider();
            var behaviors = sp.GetServices<ICommandBehavior<FakeCommand>>();

            behaviors.Should().HaveCount(1);
            behaviors.Should().ContainItemsAssignableTo<CollateralAfterBehavior<FakeCommand>>();
            sc.Should().OnlyContain(d => d.ServiceType == typeof(ICommandBehavior<>));
            sc.Should().HaveCount(1);
        }

        [Fact]
        public void MustKeepTheCommandBehaviorRegistrationWhenAGenericCollateralInterfaceIsDeclaredBeforeIt()
        {
            var sc = new ServiceCollection();

            sc.RegisterBehaviorForAllCommands(typeof(CollateralBeforeBehavior<>));

            var sp = sc.BuildServiceProvider();
            var behaviors = sp.GetServices<ICommandBehavior<FakeCommand>>();

            behaviors.Should().HaveCount(1);
            behaviors.Should().ContainItemsAssignableTo<CollateralBeforeBehavior<FakeCommand>>();
            sc.Should().OnlyContain(d => d.ServiceType == typeof(ICommandBehavior<>));
            sc.Should().HaveCount(1);
        }

        [Fact]
        public void MustNotRegisterABehaviorUnderANonGenericCollateralInterface()
        {
            var sc = new ServiceCollection();

            sc.RegisterBehaviorForAllCommands(typeof(NonGenericCollateralBaseBehavior<>));

            sc.Should().NotContain(d => d.ServiceType == typeof(IFakeNonGenericCollateral));
            sc.Should().OnlyContain(d => d.ServiceType == typeof(ICommandBehavior<>) || d.ServiceType == typeof(ICommandBehavior<FakeCommand>));
            sc.Should().HaveCount(2);

            var sp = sc.BuildServiceProvider();
            sp.GetService<IFakeNonGenericCollateral>().Should().BeNull();
            sp.GetServices<ICommandBehavior<FakeCommand>>().Should().ContainItemsAssignableTo<NonGenericCollateralBehavior>();
        }

        private class NotACommand { }
        private class FakeCommand : ICommand { }
        private class AnotherFakeCommand : ICommand { }
        private class FakeCommandBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
        private class FakeCommandBehavior3<TMessage> { }
        private interface IFakeGenericCollateral<T> { }
        private class CollateralAfterBehavior<TMessage> : ICommandBehavior<TMessage>, IFakeGenericCollateral<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
        private class CollateralBeforeBehavior<TMessage> : IFakeGenericCollateral<TMessage>, ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
        private interface IFakeNonGenericCollateral { }
        private class NonGenericCollateralBaseBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
        private class NonGenericCollateralBehavior : NonGenericCollateralBaseBehavior<FakeCommand>, IFakeNonGenericCollateral { }
    }
}
