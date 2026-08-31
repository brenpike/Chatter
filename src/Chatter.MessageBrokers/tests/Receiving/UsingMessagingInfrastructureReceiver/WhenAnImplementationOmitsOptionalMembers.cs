using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingMessagingInfrastructureReceiver
{
    /// <summary>
    /// Pins the PUBLISHED default of <see cref="IMessagingInfrastructureReceiver.WritesToErrorQueue"/> — the value
    /// every infrastructure that does NOT declare the member inherits.
    /// </summary>
    /// <remarks>
    /// INVARIANT: this is the only place the default interface member's own value is observed. Every other test
    /// reaches the member through a type that DECLARES it — the in-memory double declares an arming auto-property
    /// and RabbitMQ's receiver declares a computed one — so each of those SHADOWS the published default and none of
    /// them can see it change. Error-Queue Write Ownership is opt-IN: an infrastructure that says nothing must leave
    /// the Brokered Message Receiver owning the Error Queue write, because that is the majority behaviour and the
    /// behaviour every adapter compiled before this member existed already relies on. Flipping the default the other
    /// way would silently SUPPRESS the receiver's error-recovery action for Azure Service Bus, SQL Service Broker and
    /// every out-of-repo adapter — poison messages would stop reaching the Error Queue at all — while the rest of the
    /// suite stayed green.
    /// </remarks>
    public class WhenAnImplementationOmitsOptionalMembers : Testing.Core.Context
    {
        [Fact]
        public void MustLeaveTheErrorQueueWriteWithTheReceiver()
        {
            IMessagingInfrastructureReceiver sut = new DeclaresOnlyTheRequiredMembers();

            sut.WritesToErrorQueue.Should().BeFalse(
                "an infrastructure that does not declare Error-Queue Write Ownership does not own that write, so the receiver must keep running its own error-recovery action");
        }

        /// <summary>
        /// An infrastructure that implements ONLY the required members, inheriting every default interface member —
        /// the shape of an adapter written against the interface without knowing this member exists.
        /// </summary>
        private sealed class DeclaresOnlyTheRequiredMembers : IMessagingInfrastructureReceiver
        {
            public Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
                => throw new NotSupportedException("this double exists only to observe the published defaults");

            public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
                => throw new NotSupportedException("this double exists only to observe the published defaults");

            public Task StopReceiver()
                => throw new NotSupportedException("this double exists only to observe the published defaults");

            public Task<SettlementResult> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
                => throw new NotSupportedException("this double exists only to observe the published defaults");

            public Task<SettlementResult> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
                => throw new NotSupportedException("this double exists only to observe the published defaults");

            public Task<SettlementResult> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
                => throw new NotSupportedException("this double exists only to observe the published defaults");

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }
}
