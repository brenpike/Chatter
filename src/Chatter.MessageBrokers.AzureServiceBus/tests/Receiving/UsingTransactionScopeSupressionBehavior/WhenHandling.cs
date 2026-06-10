using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingTransactionScopeSupressionBehavior
{
    public class WhenHandling : Testing.Core.Context
    {
        private static MessageBrokerContext CreateRealContext()
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            return new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", CancellationToken.None, bodyConverter.Object);
        }

        [Fact]
        public async Task MustInvokeNextOnceWhenContextIsNotMessageBrokerContext()
        {
            var behavior = new TransactionScopeSupressionBehavior<ICommand>();
            var invocationCount = 0;
            CommandHandlerDelegate next = () => { invocationCount++; return Task.CompletedTask; };

            await behavior.Handle(Mock.Of<ICommand>(), Mock.Of<IMessageHandlerContext>(), next);

            invocationCount.Should().Be(1);
        }

        [Fact]
        public async Task MustInvokeNextOnceWhenContainerHasNoTransactionContext()
        {
            var behavior = new TransactionScopeSupressionBehavior<ICommand>();
            var context = CreateRealContext();
            var invocationCount = 0;
            CommandHandlerDelegate next = () => { invocationCount++; return Task.CompletedTask; };

            await behavior.Handle(Mock.Of<ICommand>(), context, next);

            invocationCount.Should().Be(1);
        }

        [Fact]
        public async Task MustSuppressAmbientTransactionWhenContainerHasTransactionContext()
        {
            var behavior = new TransactionScopeSupressionBehavior<ICommand>();
            var context = CreateRealContext();
            context.Container.Include(new TransactionContext("receiver-path"));
            var invocationCount = 0;
            Transaction ambientDuringNext = null;
            CommandHandlerDelegate next = () =>
            {
                invocationCount++;
                ambientDuringNext = Transaction.Current;
                return Task.CompletedTask;
            };

            await behavior.Handle(Mock.Of<ICommand>(), context, next);

            invocationCount.Should().Be(1);
            ambientDuringNext.Should().BeNull();
        }
    }
}
