using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingMessagingInfrastructureFactory
{
    public class WhenCreating : Testing.Core.Context
    {
        [Fact]
        public void MustDelegateReceiverCreationToSuppliedFactory()
        {
            var expected = Mock.Of<IMessagingInfrastructureReceiver>();
            var sut = new MessagingInfrastructureFactory(() => expected, () => Mock.Of<IMessagingInfrastructureDispatcher>());

            ((IMessagingInfrastructureReceiverFactory)sut).Create().Should().BeSameAs(expected);
        }

        [Fact]
        public void MustDelegateDispatcherCreationToSuppliedFactory()
        {
            var expected = Mock.Of<IMessagingInfrastructureDispatcher>();
            var sut = new MessagingInfrastructureFactory(() => Mock.Of<IMessagingInfrastructureReceiver>(), () => expected);

            ((IMessagingInfrastructureDispatcherFactory)sut).Create().Should().BeSameAs(expected);
        }

        [Fact]
        public void MustInvokeReceiverFactoryOnEveryCall()
        {
            var invocations = 0;
            Func<IMessagingInfrastructureReceiver> createReceiver = () =>
            {
                invocations++;
                return Mock.Of<IMessagingInfrastructureReceiver>();
            };
            var sut = new MessagingInfrastructureFactory(createReceiver, () => Mock.Of<IMessagingInfrastructureDispatcher>());

            ((IMessagingInfrastructureReceiverFactory)sut).Create();
            ((IMessagingInfrastructureReceiverFactory)sut).Create();

            invocations.Should().Be(2);
        }

        [Fact]
        public void MustInvokeDispatcherFactoryOnEveryCall()
        {
            var invocations = 0;
            Func<IMessagingInfrastructureDispatcher> createDispatcher = () =>
            {
                invocations++;
                return Mock.Of<IMessagingInfrastructureDispatcher>();
            };
            var sut = new MessagingInfrastructureFactory(() => Mock.Of<IMessagingInfrastructureReceiver>(), createDispatcher);

            ((IMessagingInfrastructureDispatcherFactory)sut).Create();
            ((IMessagingInfrastructureDispatcherFactory)sut).Create();

            invocations.Should().Be(2);
        }

        [Fact]
        public void MustThrowWhenReceiverFactoryIsNull()
        {
            Action act = () => new MessagingInfrastructureFactory(null, () => Mock.Of<IMessagingInfrastructureDispatcher>());

            act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("createReceiver");
        }

        [Fact]
        public void MustThrowWhenDispatcherFactoryIsNull()
        {
            Action act = () => new MessagingInfrastructureFactory(() => Mock.Of<IMessagingInfrastructureReceiver>(), null);

            act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("createDispatcher");
        }
    }
}
