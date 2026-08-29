using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker;
using Chatter.SqlChangeFeed.Scripts.Triggers;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Diagnostics
{
    /// <summary>
    /// Pins ADR-0010's "Propagation scope" section for the Change Feed, where the two documented propagation gaps
    /// COMPOUND on one receive path, and pins the payoff of instrumenting the per-delivery worker seam rather than
    /// the scoped received-message dispatcher.
    /// </summary>
    /// <remarks>
    /// A Change Feed message has no producer: it is raised by a SQL trigger that sends
    /// <c>MESSAGE TYPE [DEFAULT]</c>, so (1) there are no producer headers to propagate, and (2) the delivery lands
    /// on <c>SqlServiceBrokerReceiver</c>'s DefaultType branch, which builds a fresh header dictionary and would
    /// drop any header that did exist. Both limitations are PRE-EXISTING, affect EVERY header rather than just
    /// trace context, and are pinned here so a future change that fixes or worsens either becomes visible.
    /// Despite <see cref="ChangeFeedReceiver{TRowChangeData}"/> overriding
    /// <c>DispatchReceivedMessageAsync</c> and never reaching the scoped received-message dispatcher, a receive
    /// span IS still produced, because the instrumentation sits at the per-delivery worker seam in
    /// <see cref="BrokeredMessageReceiver{TMessage}"/>. That is the whole reason the worker was chosen as the seam,
    /// and it is pinned below.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenChangeFeedMessagesAreProcessed : Testing.Core.Context
    {
        private const string SchemaName = "dbo";
        private static readonly TimeSpan _watchdogTimeout = TimeSpan.FromSeconds(15);

        // -----------------------------------------------------------------------------------------------
        // ORIGIN — the trigger sends MESSAGE TYPE [DEFAULT], so a Change Feed delivery is routed onto exactly
        // the context-dropping SqlServiceBroker receive path.
        // -----------------------------------------------------------------------------------------------

        [Fact]
        public void MustRaiseChangeFeedNotificationsAsDefaultTypedServiceBrokerMessages()
        {
            var trigger = new CreateChangeFeedTrigger("ChangeFeedTable", "ChangeFeedTrigger", ChangeTypes.Insert, "ChangeFeedService", SchemaName).ToString();

            trigger.Should().Contain($"MESSAGE TYPE [{ServicesMessageTypes.DefaultType}]",
                "the Change Feed trigger sends DEFAULT-typed messages, which is the SqlServiceBroker branch that drops all upstream context");
            trigger.Should().NotContain(ServicesMessageTypes.ChatterBrokeredMessageType,
                "the trigger emits no Chatter envelope, so there is no envelope MessageContext for a receiver to adopt");
        }

        [Fact]
        public void MustEmitNoTraceContextFromTheChangeFeedTrigger()
        {
            var trigger = new CreateChangeFeedTrigger("ChangeFeedTable", "ChangeFeedTrigger", ChangeTypes.Update, "ChangeFeedService", SchemaName).ToString();

            trigger.Should().NotContain(TraceContextHeaders.TraceParent);
            trigger.Should().NotContain(TraceContextHeaders.TraceState);
        }

        // -----------------------------------------------------------------------------------------------
        // COMPOUNDED LOSS — a Change Feed delivery reaches the receiver carrying only the SqlServiceBroker
        // stamped keys, so there is no remote parent to extract.
        // -----------------------------------------------------------------------------------------------

        [Fact]
        public void MustCarryNoTraceContextOnAChangeFeedDelivery()
        {
            var headers = BuildChangeFeedDeliveryHeaders();

            headers.Should().NotContainKey(TraceContextHeaders.TraceParent);
            headers.Should().NotContainKey(TraceContextHeaders.TraceState);
        }

        [Fact]
        public void MustExtractNoRemoteParentFromAChangeFeedDelivery()
        {
            TraceContextPropagator.TryExtract(BuildChangeFeedDeliveryHeaders(), out var extracted)
                                  .Should().BeFalse("a trigger-raised change has no producer, so no trace context exists to extract");

            extracted.Should().Be(default(ActivityContext));
        }

        // -----------------------------------------------------------------------------------------------
        // THE WORKER-SEAM PAYOFF — a receive span is produced even though the scoped received-message
        // dispatcher is bypassed entirely, and it is a fresh root because there is no inbound context.
        // -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task MustStartAReceiveSpanEvenThoughTheScopedDispatcherIsBypassed()
        {
            var delivery = await RunOneChangeFeedDeliveryAsync();

            delivery.ReceiveSpans.Should().ContainSingle(
                "the span is opened at the per-delivery worker seam, which every delivery crosses regardless of how DispatchReceivedMessageAsync is overridden");
        }

        [Fact]
        public async Task MustBypassTheScopedReceivedMessageDispatcherWhileStillProducingTheSpan()
        {
            var delivery = await RunOneChangeFeedDeliveryAsync();

            delivery.ScopedDispatcher.Verify(
                dispatcher => dispatcher.DispatchAsync(It.IsAny<ProcessChangeFeedCommand<FakeRowData>>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()),
                Times.Never);

            delivery.MessageDispatcher.Verify(
                dispatcher => dispatcher.Dispatch(It.IsAny<RowInsertedEvent<FakeRowData>>(), It.IsAny<IMessageHandlerContext>()),
                Times.Once);

            delivery.ReceiveSpans.Should().ContainSingle();
        }

        [Fact]
        public async Task MustStartTheReceiveSpanAsAFreshRoot()
        {
            var delivery = await RunOneChangeFeedDeliveryAsync();

            var receiveSpan = delivery.ReceiveSpans.Single();

            receiveSpan.Parent.Should().BeNull("there is no inbound trace context for a trigger-raised change to parent to");
            receiveSpan.ParentSpanId.Should().Be(default(ActivitySpanId));
            receiveSpan.Links.Should().BeEmpty("an ambient activity is linked only when a remote parent was extracted");
        }

        [Fact]
        public async Task MustTagTheReceiveSpanWithTheBrokerOperationAttributes()
        {
            var delivery = await RunOneChangeFeedDeliveryAsync();

            var receiveSpan = delivery.ReceiveSpans.Single();

            receiveSpan.Kind.Should().Be(ActivityKind.Consumer);
            receiveSpan.GetTagItem(BrokerDiagnostics.OperationType).Should().Be(BrokerDiagnostics.OperationTypes.Receive);
            receiveSpan.GetTagItem(BrokerDiagnostics.DestinationName).Should().Be(delivery.MessageReceiverPath);
        }

        // -----------------------------------------------------------------------------------------------
        // Harness
        // -----------------------------------------------------------------------------------------------

        // The header set a Change Feed delivery actually arrives with: SqlServiceBrokerReceiver's DefaultType
        // branch builds a fresh dictionary and stamps only these SqlServiceBroker and core keys. Nothing a producer
        // could have supplied — trace context included — is representable here.
        private static Dictionary<string, object> BuildChangeFeedDeliveryHeaders()
            => new Dictionary<string, object>
            {
                [SSBMessageContext.ConversationGroupId] = Guid.Parse("0b3f1d92-6c47-4a51-9f2a-1d0e8c7b4a35"),
                [SSBMessageContext.ConversationHandle] = Guid.Parse("7c9e2f04-5b18-4d63-8a72-3e6f1a9c0d48"),
                [SSBMessageContext.MessageSequenceNumber] = 1L,
                [SSBMessageContext.ServiceName] = "ChangeFeedService",
                [SSBMessageContext.ServiceContractName] = ServicesMessageTypes.ChatterServiceContract,
                [SSBMessageContext.MessageTypeName] = ServicesMessageTypes.DefaultType,
                [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType,
                [MessageContext.ReceiveAttempts] = 1,
            };

        // Drives ONE Change Feed delivery end to end through the real BrokeredMessageReceiver worker seam with a
        // .NET ActivityListener attached to Chatter's own broker ActivitySource, then quiesces the receiver. The
        // receiver path is unique per run so a concurrently-running receiver elsewhere in this assembly cannot
        // contribute spans to the assertions.
        private static async Task<ChangeFeedDeliveryResult> RunOneChangeFeedDeliveryAsync()
        {
            var messageReceiverPath = $"change-feed-trace-{Guid.NewGuid():n}";
            var scopedDispatcher = new Mock<IReceivedMessageDispatcher>();
            var messageDispatcher = new Mock<IMessageDispatcher>();

            using var recordingScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName);

            var infrastructureReceiver = new SingleDeliveryInfrastructureReceiver(BuildChangeFeedDelivery(messageReceiverPath));
            var receiver = CreateChangeFeedReceiver(infrastructureReceiver, scopedDispatcher, messageDispatcher);

            // StartReceiver awaits the receive loop, so it must not be awaited before the receiver is stopped.
            var receiving = receiver.StartReceiver(BuildReceiverOptions(messageReceiverPath));

            // Every await is watchdogged so a teardown regression surfaces as a prompt TimeoutException rather than
            // as a hung test run.
            await infrastructureReceiver.Acknowledged.WaitAsync(_watchdogTimeout);
            await receiver.StopReceiver().WaitAsync(_watchdogTimeout);
            await receiving.WaitAsync(_watchdogTimeout);

            var receiveSpans = recordingScope
                .StoppedNamed($"{BrokerDiagnostics.OperationTypes.Receive} {messageReceiverPath}")
                .ToArray();

            return new ChangeFeedDeliveryResult(messageReceiverPath, receiveSpans, scopedDispatcher, messageDispatcher);
        }

        private static MessageBrokerContext BuildChangeFeedDelivery(string messageReceiverPath)
        {
            var command = new ProcessChangeFeedCommand<FakeRowData>
            {
                Changes = new[]
                {
                    new ChangeFeedItem<FakeRowData> { Inserted = new FakeRowData { Id = 1, Name = "inserted" }, Deleted = null },
                },
            };

            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.Setup(converter => converter.Convert<ProcessChangeFeedCommand<FakeRowData>>(It.IsAny<byte[]>())).Returns(command);

            return new MessageBrokerContext(
                messageId: Guid.NewGuid().ToString(),
                body: Array.Empty<byte>(),
                applicationProperties: BuildChangeFeedDeliveryHeaders(),
                messageReceiverPath: messageReceiverPath,
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: bodyConverter.Object);
        }

        private static ChangeFeedReceiver<FakeRowData> CreateChangeFeedReceiver(
            SingleDeliveryInfrastructureReceiver infrastructureReceiver,
            Mock<IReceivedMessageDispatcher> scopedDispatcher,
            Mock<IMessageDispatcher> messageDispatcher)
        {
            var serviceProvider = new Mock<IServiceProvider>();
            serviceProvider.Setup(provider => provider.GetService(typeof(IMessageDispatcher))).Returns(messageDispatcher.Object);
            serviceProvider.Setup(provider => provider.GetService(typeof(IBrokeredMessageDispatcher))).Returns(new Mock<IBrokeredMessageDispatcher>().Object);

            var scope = new Mock<IServiceScope>();
            scope.SetupGet(created => created.ServiceProvider).Returns(serviceProvider.Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(factory => factory.CreateScope()).Returns(scope.Object);

            return new ChangeFeedReceiver<FakeRowData>(
                new SingleReceiverInfrastructureProvider(infrastructureReceiver),
                new MessageBrokerOptions(),
                NullLogger<BrokeredMessageReceiver<ProcessChangeFeedCommand<FakeRowData>>>.Instance,
                scopeFactory.Object,
                new Mock<IMaxReceivesExceededAction>().Object,
                new Mock<ICriticalFailureNotifier>().Object,
                new PassThroughRecoveryStrategy(),
                scopedDispatcher.Object);
        }

        private static ReceiverOptions BuildReceiverOptions(string messageReceiverPath)
            => new ReceiverOptions
            {
                InfrastructureType = SingleReceiverInfrastructureProvider.InfrastructureType,
                MessageReceiverPath = messageReceiverPath,
                SendingPath = messageReceiverPath,
                ErrorQueuePath = "change-feed-error-queue",
                DeadLetterQueuePath = "change-feed-deadletter-queue",
                TransactionMode = TransactionMode.None,
                MaxReceiveAttempts = 10,
                MaxConcurrentCalls = 1,
            };

        /// <summary>What one driven Change Feed delivery produced, for the assertions above to read.</summary>
        private sealed class ChangeFeedDeliveryResult
        {
            internal ChangeFeedDeliveryResult(
                string messageReceiverPath,
                IReadOnlyList<Activity> receiveSpans,
                Mock<IReceivedMessageDispatcher> scopedDispatcher,
                Mock<IMessageDispatcher> messageDispatcher)
            {
                MessageReceiverPath = messageReceiverPath;
                ReceiveSpans = receiveSpans;
                ScopedDispatcher = scopedDispatcher;
                MessageDispatcher = messageDispatcher;
            }

            internal string MessageReceiverPath { get; }

            internal IReadOnlyList<Activity> ReceiveSpans { get; }

            internal Mock<IReceivedMessageDispatcher> ScopedDispatcher { get; }

            internal Mock<IMessageDispatcher> MessageDispatcher { get; }
        }

        /// <summary>Invokes the supplied action exactly once, with no retry machinery of its own.</summary>
        private sealed class PassThroughRecoveryStrategy : IRecoveryStrategy
        {
            public Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken token) => action();
        }

        /// <summary>
        /// Hands the receive loop ONE delivery and then parks until the loop token is cancelled, so the loop neither
        /// spins nor delivers twice. Signals <see cref="Acknowledged"/> when the receiver settles that delivery.
        /// </summary>
        private sealed class SingleDeliveryInfrastructureReceiver : IMessagingInfrastructureReceiver
        {
            private readonly MessageBrokerContext _delivery;
            private readonly TaskCompletionSource<bool> _acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _receiveCallCount;

            internal SingleDeliveryInfrastructureReceiver(MessageBrokerContext delivery) => _delivery = delivery;

            /// <summary>Completes when the receiver acknowledges the single delivery.</summary>
            internal Task Acknowledged => _acknowledged.Task;

            public Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
                => Interlocked.Increment(ref _receiveCallCount) == 1
                    ? Task.FromResult(_delivery)
                    : ParkUntilCancelledAsync(cancellationToken);

            public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopReceiver() => Task.CompletedTask;

            public Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
            {
                _acknowledged.TrySetResult(true);
                return Task.FromResult(true);
            }

            public Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
                => Task.FromResult(true);

            public Task<bool> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
                => Task.FromResult(true);

            public Task<int> MessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken)
                => Task.FromResult(1);

            public TransactionScope CreateLocalTransaction(TransactionContext context) => null;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;

            // An empty receive that returns immediately would spin the loop hot; parking on the loop token mirrors a
            // real broker's long-poll and returns a null delivery once teardown cancels it.
            private static async Task<MessageBrokerContext> ParkUntilCancelledAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }

                return null;
            }
        }

        /// <summary>Resolves every lookup to the one <see cref="SingleDeliveryInfrastructureReceiver"/> under test.</summary>
        private sealed class SingleReceiverInfrastructureProvider : IMessagingInfrastructureProvider
        {
            internal const string InfrastructureType = "change-feed-diagnostics-test";

            private readonly IMessagingInfrastructureReceiver _receiver;
            private readonly IMessagingInfrastructure _infrastructure;

            internal SingleReceiverInfrastructureProvider(IMessagingInfrastructureReceiver receiver)
            {
                _receiver = receiver;

                var receiverFactory = new Mock<IMessagingInfrastructureReceiverFactory>();
                receiverFactory.Setup(factory => factory.Create()).Returns(receiver);

                var dispatcherFactory = new Mock<IMessagingInfrastructureDispatcherFactory>();
                dispatcherFactory.Setup(factory => factory.Create()).Returns(new Mock<IMessagingInfrastructureDispatcher>().Object);

                _infrastructure = new MessagingInfrastructure(InfrastructureType, receiverFactory.Object, dispatcherFactory.Object);
            }

            public IMessagingInfrastructure GetInfrastructure(string type) => _infrastructure;

            public IMessagingInfrastructureReceiver GetReceiver(string type) => _receiver;

            public IMessagingInfrastructureDispatcher GetDispatcher(string type) => _infrastructure.DispatchInfrastructure;
        }
    }
}
