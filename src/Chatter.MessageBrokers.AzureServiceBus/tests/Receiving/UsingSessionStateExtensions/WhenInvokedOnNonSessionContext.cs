using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingSessionStateExtensions
{
    // Pins the session-state misuse guard: invoking Get/Set/ClearSessionStateAsync while handling a message
    // that was NOT received through a session-enabled receiver fails fast with InvalidOperationException and
    // the documented message, rather than a silent no-op or a NullReferenceException. A non-session context is
    // modeled by a MessageBrokerContext whose container holds no TransactionContext (and therefore no held
    // ServiceBusSessionReceiver) — exactly the shape a non-session receive produces.
    public class WhenInvokedOnNonSessionContext : Testing.Core.Context
    {
        private const string _expectedMessage =
            "Azure Service Bus session state is only available for session-enabled receivers.";

        private static MessageBrokerContext NonSessionContext()
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            return new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", CancellationToken.None, bodyConverter.Object);
        }

        [Fact]
        public async Task MustThrowOnGetSessionState()
        {
            var context = NonSessionContext();

            Func<Task> act = () => context.GetSessionStateAsync();

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(_expectedMessage);
        }

        [Fact]
        public async Task MustThrowOnSetSessionState()
        {
            var context = NonSessionContext();

            Func<Task> act = () => context.SetSessionStateAsync(new BinaryData(new byte[] { 1 }));

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(_expectedMessage);
        }

        [Fact]
        public async Task MustThrowOnClearSessionState()
        {
            var context = NonSessionContext();

            Func<Task> act = () => context.ClearSessionStateAsync();

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(_expectedMessage);
        }
    }
}
