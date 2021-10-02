using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingServiceCollectionExtensions
{
    public class WhenMovingServiceDescriptorBefore
    {
        public WhenMovingServiceDescriptorBefore() { }

        [Fact]
        public void ShouldThrowIfTypeOfServiceToMoveIsNull()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.MoveServiceDescriptorBefore(null, typeof(AnotherFakeCommand))).Should().ThrowExactly<ArgumentNullException>().WithMessage($"The type of {nameof(ServiceDescriptor)} to move cannot be null*");
        }

        [Fact]
        public void ShouldThrowIfTypeOfServiceToInsertBeforeIsNull()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.MoveServiceDescriptorBefore(typeof(FakeCommand), null)).Should().ThrowExactly<ArgumentNullException>().WithMessage($"The type of {nameof(ServiceDescriptor)} to move services of type*");
        }

        [Fact]
        public void MustBeNoOpIfServiceToInsertBeforeDoesNotExistInServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.Should().BeEmpty();
            sc.MoveServiceDescriptorBefore(typeof(FakeCommand), typeof(AnotherFakeCommand));
            sc.Should().BeEmpty();
        }

        [Fact]
        public void MustBeNoOpIfServiceToMoveDoesNotExistInServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.AddTransient(typeof(FakeCommand));
            sc.Should().HaveCount(1);
            sc.MoveServiceDescriptorBefore(typeof(AnotherFakeCommand), typeof(FakeCommand));
            sc.Should().OnlyContain(sd => sd.ServiceType == typeof(FakeCommand));
            sc.Should().HaveCount(1);
        }

        [Fact]
        public void MustBeNoOpIfAllServicesToMoveAreAlreadyBeforeServiceToInsertBefore()
        {
            var sc = new ServiceCollection();
            sc.AddTransient(typeof(AnotherFakeCommand));
            sc.AddTransient(typeof(FakeCommand));
            var sd1 = sc[0];
            var sd2 = sc[1];
            sc.Should().HaveCount(2);
            sc.Should().ContainInOrder(sd1, sd2);
            sc.MoveServiceDescriptorBefore(typeof(AnotherFakeCommand), typeof(FakeCommand));
            sc.Should().ContainInOrder(sd1, sd2);
            sc.Should().HaveCount(2);
        }

        [Fact]
        public void MustMoveServiceDescriptorsThatAreAfterServiceToInsertBefore()
        {
            var sc = new ServiceCollection();
            sc.AddTransient(typeof(FakeCommand));
            sc.AddTransient(typeof(AnotherFakeCommand));
            sc.AddTransient(typeof(FakeCommand));
            var sd1 = sc[0];
            var sd2 = sc[1];
            var sd3 = sc[2];
            sc.Should().ContainInOrder(sd1, sd2, sd3);
            sc.MoveServiceDescriptorBefore(typeof(FakeCommand), typeof(AnotherFakeCommand));
            sc.Should().ContainInOrder(sd1, sd3, sd2);
        }

        private class NotACommand { }
        private class FakeCommand : ICommand { }
        private class AnotherFakeCommand : ICommand { }
    }
}
