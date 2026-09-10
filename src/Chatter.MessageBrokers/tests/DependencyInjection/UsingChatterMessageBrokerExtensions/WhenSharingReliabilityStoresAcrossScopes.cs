#nullable disable

using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.DependencyInjection.UsingChatterMessageBrokerExtensions
{
    /// <summary>
    /// Drives the REAL <c>AddMessageBrokers</c> graph through <see cref="IServiceScopeFactory"/> the way every
    /// consumer of a reliability store drives it: <c>BrokeredMessageOutboxProcessor</c> opens a fresh scope per poll
    /// and <c>ScopedReceivedMessageDispatcher</c> opens a fresh scope per delivery, so no two operations ever share
    /// one scope.
    /// </summary>
    /// <remarks>
    /// ORACLE: the shipped in-memory stores keep their whole state in a per-instance
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>, so an instance whose lifetime
    /// is shorter than that state's is a store that remembers nothing. These facts read the OBSERVABLE consequence —
    /// a row written in one scope reaching the drain resolved in another, and a duplicate id received in a second
    /// scope being skipped — never a descriptor's declared lifetime.
    /// INVARIANT: every provider here is built with <see cref="ServiceProviderOptions.ValidateScopes"/> on, exactly as
    /// a host builds it, so a promoted process-lifetime store that captured a scoped dependency throws at the first
    /// resolution rather than silently holding a dead graph for the process lifetime.
    /// The circuit breaker state store is deliberately NOT asserted on here: its state already spans its owner (the
    /// Receiver Scope the receive loop opens once), and fusing it across receivers would let one queue's broker
    /// outage open another queue's circuit.
    /// </remarks>
    public class WhenSharingReliabilityStoresAcrossScopes : Testing.Core.Context
    {
        private const string DuplicateMessageId = "duplicate-message-id";
        private const string DrainedMessageId = "drained-message-id";
        private const string Destination = "destination";
        private const string MessageBody = "payload";

        private static readonly System.Reflection.Assembly NoBrokeredMessageAssembly = typeof(IMessage).Assembly;

        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IMessagingInfrastructureDispatcher> _dispatcher = new Mock<IMessagingInfrastructureDispatcher>();

        public WhenSharingReliabilityStoresAcrossScopes()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                       .Returns(Task.CompletedTask);
        }

        // ------------------------------------------------------------------ fakes

        // A custom inbox registered by the application BEFORE AddMessageBrokers, with a lifetime the application
        // chose. Promotion must not reach it.
        private sealed class CustomScopedInbox : IBrokeredMessageInbox
        {
            public Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver)
                => messageReceiver();
        }

        // ------------------------------------------------------------------ infrastructure

        // Builds the Chatter DI graph with an optional pre-registration action that runs before AddMessageBrokers so
        // callers can register a custom store and prove promotion is default-only.
        private ServiceProvider BuildProvider(Action<IServiceCollection> preRegister = null)
        {
            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddSingleton<IMessagingInfrastructure>(BuildInMemoryInfrastructure());

            preRegister?.Invoke(services);

            services
                .AddChatterCqrs(configuration, NoBrokeredMessageAssembly)
                .AddMessageBrokers(
                    optionsBuilder: null,
                    receiverHandlerSourceBuilder: b => b.WithExplicitAssemblies(NoBrokeredMessageAssembly));

            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        // Opens a scope the way production opens one — from the scope factory, off the root provider — so the store
        // instances these facts compare are the instances the poll loop and the delivery dispatcher would resolve.
        private static IServiceScope OpenScope(IServiceProvider provider)
            => provider.GetRequiredService<IServiceScopeFactory>().CreateScope();

        private IMessagingInfrastructure BuildInMemoryInfrastructure()
        {
            var dispatcherFactory = new Mock<IMessagingInfrastructureDispatcherFactory>();
            dispatcherFactory.Setup(f => f.Create()).Returns(_dispatcher.Object);
            var receiverFactory = new Mock<IMessagingInfrastructureReceiverFactory>();
            receiverFactory.Setup(f => f.Create()).Returns(new Mock<IMessagingInfrastructureReceiver>().Object);
            return new MessagingInfrastructure(
                type: InMemoryMessagingInfrastructureProvider.InfrastructureType,
                receiveInfrastructure: receiverFactory.Object,
                dispatchInfrastructure: dispatcherFactory.Object);
        }

        private IMessageBrokerContext CreateBrokerContext(string messageId)
        {
            var inbound = new InboundBrokeredMessage(messageId, new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", _bodyConverter.Object);
            var context = new Mock<IMessageBrokerContext>();
            context.SetupGet(c => c.BrokeredMessage).Returns(inbound);
            return context.Object;
        }

        // The row is written the way a sender writes it, naming the Messaging Infrastructure the drain must look up.
        private static OutboundBrokeredMessage BuildOutboundMessage()
        {
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InMemoryMessagingInfrastructureProvider.InfrastructureType
            };

            return new OutboundBrokeredMessage(DrainedMessageId, MessageBody, messageContext, Destination, new JsonBodyConverter());
        }

        // ------------------------------------------------------------------ inbox

        [Fact]
        public void InboxDefault_ResolvesSameInstanceAcrossScopes()
        {
            using var provider = BuildProvider();
            using var receivingScope = OpenScope(provider);
            using var redeliveryScope = OpenScope(provider);

            var receivingInbox = receivingScope.ServiceProvider.GetRequiredService<IBrokeredMessageInbox>();
            var redeliveryInbox = redeliveryScope.ServiceProvider.GetRequiredService<IBrokeredMessageInbox>();

            receivingInbox.Should().BeOfType<InMemoryBrokeredMessageInbox>();
            ReferenceEquals(receivingInbox, redeliveryInbox).Should().BeTrue("the shipped inbox's dictionary is the process's only record of what has been received, and every delivery is dispatched in its own scope");
        }

        [Fact]
        public async Task InboxDefault_SkipsADuplicateReceivedInASecondScope()
        {
            using var provider = BuildProvider();
            var receiptCount = 0;

            Task CountReceipt()
            {
                receiptCount++;
                return Task.CompletedTask;
            }

            using (var receivingScope = OpenScope(provider))
            {
                await receivingScope.ServiceProvider.GetRequiredService<IBrokeredMessageInbox>()
                    .ReceiveViaInbox<object>(new object(), CreateBrokerContext(DuplicateMessageId), CountReceipt);
            }

            using (var redeliveryScope = OpenScope(provider))
            {
                await redeliveryScope.ServiceProvider.GetRequiredService<IBrokeredMessageInbox>()
                    .ReceiveViaInbox<object>(new object(), CreateBrokerContext(DuplicateMessageId), CountReceipt);
            }

            receiptCount.Should().Be(1, "a redelivery arrives in a new scope, so an inbox that cannot see the first receipt deduplicates nothing");
        }

        [Fact]
        public void CustomScopedInbox_KeepsItsOwnLifetime()
        {
            using var provider = BuildProvider(services => services.AddScoped<IBrokeredMessageInbox, CustomScopedInbox>());
            using var receivingScope = OpenScope(provider);
            using var redeliveryScope = OpenScope(provider);

            var receivingInbox = receivingScope.ServiceProvider.GetRequiredService<IBrokeredMessageInbox>();
            var redeliveryInbox = redeliveryScope.ServiceProvider.GetRequiredService<IBrokeredMessageInbox>();

            receivingInbox.Should().BeOfType<CustomScopedInbox>("the presence gate leaves an application's own store registered");
            ReferenceEquals(receivingInbox, redeliveryInbox).Should().BeFalse("promotion applies to the shipped default only — a custom store keeps the lifetime the application registered it with");
        }

        // ------------------------------------------------------------------ outbox

        [Fact]
        public void OutboxDefault_ResolvesSameInstanceAcrossScopes()
        {
            using var provider = BuildProvider();
            using var sendingScope = OpenScope(provider);
            using var drainingScope = OpenScope(provider);

            var sendingOutbox = sendingScope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>();
            var drainingOutbox = drainingScope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>();

            sendingOutbox.Should().BeOfType<InMemoryBrokeredMessageOutbox>();
            ReferenceEquals(sendingOutbox, drainingOutbox).Should().BeTrue("the shipped outbox's dictionary is the process's only copy of the rows, and the poller drains from its own scope");
        }

        [Fact]
        public async Task OutboxDefault_RowSentInOneScopeIsDrainedByTheProcessorResolvedInAnother()
        {
            using var provider = BuildProvider();

            using (var sendingScope = OpenScope(provider))
            {
                await sendingScope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>()
                    .SendToOutbox(new[] { BuildOutboundMessage() }, null);
            }

            using var drainingScope = OpenScope(provider);
            var pollableStore = (IPollableOutboxStore)drainingScope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>();
            var processor = drainingScope.ServiceProvider.GetRequiredService<IOutboxProcessor>();

            var unprocessed = await pollableStore.GetUnprocessedMessagesFromOutbox();
            unprocessed.Should().ContainSingle("the row was written in the sending scope and the poll runs in its own scope").Which.MessageId.Should().Be(DrainedMessageId);

            await processor.Process(unprocessed.Single());

            _dispatcher.Verify(d => d.Dispatch(It.Is<OutboundBrokeredMessage>(m => m.MessageId == DrainedMessageId), null), Times.Once);
            (await pollableStore.GetUnprocessedMessagesFromOutbox()).Should().BeEmpty("the drained row must not be returned to a later poll");
        }
    }
}
