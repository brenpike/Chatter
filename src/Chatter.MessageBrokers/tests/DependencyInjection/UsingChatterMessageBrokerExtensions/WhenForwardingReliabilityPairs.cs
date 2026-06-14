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
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.DependencyInjection.UsingChatterMessageBrokerExtensions
{
    /// <summary>
    /// Verifies the closed-by-construction reliability-pair registration contract: the outbox pair
    /// (<see cref="IBrokeredMessageOutbox"/> / <see cref="IPollableOutboxStore"/>) and the inbox pair
    /// (<see cref="IBrokeredMessageInbox"/> / <see cref="IInboxDeduplicator"/>) must always resolve both
    /// facets to the same scoped instance so split-store is unrepresentable.
    ///
    /// INVARIANT: all cases resolve both facets within a single <see cref="IServiceScope"/> — cross-scope
    /// resolution would give different scoped instances by definition and is not a split-store defect.
    /// </summary>
    public class WhenForwardingReliabilityPairs : Testing.Core.Context
    {
        // ------------------------------------------------------------------ fakes

        private sealed class CustomOutboxBoth : IBrokeredMessageOutbox, IPollableOutboxStore
        {
            public Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default)
                => Task.FromResult<IEnumerable<OutboxMessage>>(Array.Empty<OutboxMessage>());

            public Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId, CancellationToken cancellationToken = default)
                => Task.FromResult<IEnumerable<OutboxMessage>>(Array.Empty<OutboxMessage>());
        }

        // Implements ONLY IBrokeredMessageOutbox — resolving IPollableOutboxStore via cast must throw.
        private sealed class CustomOutboxPrimaryOnly : IBrokeredMessageOutbox
        {
            public Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }

        private sealed class CustomInboxBoth : IBrokeredMessageInbox, IInboxDeduplicator
        {
            public Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver)
                => messageReceiver();

            public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
                => Task.FromResult(false);
        }

        // Implements ONLY IBrokeredMessageInbox — resolving IInboxDeduplicator via cast must throw.
        private sealed class CustomInboxPrimaryOnly : IBrokeredMessageInbox
        {
            public Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver)
                => messageReceiver();
        }

        // ------------------------------------------------------------------ infrastructure

        private static readonly System.Reflection.Assembly NoBrokeredMessageAssembly = typeof(IMessage).Assembly;

        // Builds the Chatter DI graph with an optional pre-registration action that runs before
        // AddMessageBrokers so callers can register custom implementations to test the override seams.
        private static IServiceScope BuildScope(Action<IServiceCollection> preRegister = null)
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

            return services.BuildServiceProvider().CreateScope();
        }

        private static IMessagingInfrastructure BuildInMemoryInfrastructure()
        {
            // No receiver is started in these registration-only tests; the factories are mocked so
            // no actual InMemoryMessagingInfrastructureReceiver is allocated or disposed here.
            var dispatcherFactory = new Mock<IMessagingInfrastructureDispatcherFactory>();
            dispatcherFactory.Setup(f => f.Create()).Returns(new Mock<IMessagingInfrastructureDispatcher>().Object);
            var receiverFactory = new Mock<IMessagingInfrastructureReceiverFactory>();
            receiverFactory.Setup(f => f.Create()).Returns(new Mock<IMessagingInfrastructureReceiver>().Object);
            return new MessagingInfrastructure(
                type: InMemoryMessagingInfrastructureProvider.InfrastructureType,
                receiveInfrastructure: receiverFactory.Object,
                dispatchInfrastructure: dispatcherFactory.Object);
        }

        // ------------------------------------------------------------------ outbox pair

        [Fact]
        public void OutboxDefault_BothFacetsResolveToSameInMemoryInstance()
        {
            using var scope = BuildScope();
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();
            var pollable = sp.GetRequiredService<IPollableOutboxStore>();

            outbox.Should().BeOfType<InMemoryBrokeredMessageOutbox>();
            ReferenceEquals(outbox, pollable).Should().BeTrue("both facets must resolve to the same scoped instance");
        }

        [Fact]
        public void OutboxCustomPrimaryImplementingBoth_SecondaryResolvesToSameCustomInstance()
        {
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageOutbox, CustomOutboxBoth>());
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();
            var pollable = sp.GetRequiredService<IPollableOutboxStore>();

            outbox.Should().BeOfType<CustomOutboxBoth>();
            ReferenceEquals(outbox, pollable).Should().BeTrue("secondary must track the same custom primary instance");
        }

        [Fact]
        public void OutboxCustomPrimaryNotImplementingSecondary_ResolvingSecondaryThrows()
        {
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageOutbox, CustomOutboxPrimaryOnly>());
            var sp = scope.ServiceProvider;

            // The cast in the forward factory throws InvalidCastException (possibly wrapped) at resolution.
            Action resolveSecondary = () => sp.GetRequiredService<IPollableOutboxStore>();
            resolveSecondary.Should().Throw<Exception>("a custom primary that does not implement the secondary must fail loudly");
        }

        [Fact]
        public void OutboxCustomSecondaryOnlyNoPrimary_BothFacetsResolveToInMemoryConcrete()
        {
            // INVARIANT: a lone custom secondary with no custom primary must NOT produce a split.
            // The default branch overrides the lone secondary and forwards both to in-memory.
            using var scope = BuildScope(services =>
                services.AddScoped<IPollableOutboxStore, CustomOutboxBoth>());
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();
            var pollable = sp.GetRequiredService<IPollableOutboxStore>();

            outbox.Should().BeOfType<InMemoryBrokeredMessageOutbox>("default branch must override lone custom secondary");
            pollable.Should().BeOfType<InMemoryBrokeredMessageOutbox>("default branch must override lone custom secondary");
            ReferenceEquals(outbox, pollable).Should().BeTrue("both facets must resolve to the same in-memory instance");
        }

        [Fact]
        public void OutboxCustomBothFacetsRegisteredByConsumer_BothResolveToConsumerInstance()
        {
            using var scope = BuildScope(services =>
            {
                services.AddScoped<CustomOutboxBoth>();
                services.AddScoped<IBrokeredMessageOutbox>(sp => sp.GetRequiredService<CustomOutboxBoth>());
                services.AddScoped<IPollableOutboxStore>(sp => sp.GetRequiredService<CustomOutboxBoth>());
            });
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();
            var pollable = sp.GetRequiredService<IPollableOutboxStore>();

            outbox.Should().BeOfType<CustomOutboxBoth>();
            ReferenceEquals(outbox, pollable).Should().BeTrue("consumer-registered pair must remain intact");
        }

        // ------------------------------------------------------------------ inbox pair

        [Fact]
        public void InboxDefault_BothFacetsResolveToSameInMemoryInstance()
        {
            using var scope = BuildScope();
            var sp = scope.ServiceProvider;

            var inbox = sp.GetRequiredService<IBrokeredMessageInbox>();
            var dedup = sp.GetRequiredService<IInboxDeduplicator>();

            inbox.Should().BeOfType<InMemoryBrokeredMessageInbox>();
            ReferenceEquals(inbox, dedup).Should().BeTrue("both facets must resolve to the same scoped instance");
        }

        [Fact]
        public void InboxCustomPrimaryImplementingBoth_SecondaryResolvesToSameCustomInstance()
        {
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageInbox, CustomInboxBoth>());
            var sp = scope.ServiceProvider;

            var inbox = sp.GetRequiredService<IBrokeredMessageInbox>();
            var dedup = sp.GetRequiredService<IInboxDeduplicator>();

            inbox.Should().BeOfType<CustomInboxBoth>();
            ReferenceEquals(inbox, dedup).Should().BeTrue("secondary must track the same custom primary instance");
        }

        [Fact]
        public void InboxCustomPrimaryNotImplementingSecondary_ResolvingSecondaryThrows()
        {
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageInbox, CustomInboxPrimaryOnly>());
            var sp = scope.ServiceProvider;

            // The cast in the forward factory throws InvalidCastException (possibly wrapped) at resolution.
            Action resolveSecondary = () => sp.GetRequiredService<IInboxDeduplicator>();
            resolveSecondary.Should().Throw<Exception>("a custom primary that does not implement the secondary must fail loudly");
        }

        [Fact]
        public void InboxCustomSecondaryOnlyNoPrimary_BothFacetsResolveToInMemoryConcrete()
        {
            // INVARIANT: a lone custom secondary with no custom primary must NOT produce a split.
            // The default branch overrides the lone secondary and forwards both to in-memory.
            using var scope = BuildScope(services =>
                services.AddScoped<IInboxDeduplicator, CustomInboxBoth>());
            var sp = scope.ServiceProvider;

            var inbox = sp.GetRequiredService<IBrokeredMessageInbox>();
            var dedup = sp.GetRequiredService<IInboxDeduplicator>();

            inbox.Should().BeOfType<InMemoryBrokeredMessageInbox>("default branch must override lone custom secondary");
            dedup.Should().BeOfType<InMemoryBrokeredMessageInbox>("default branch must override lone custom secondary");
            ReferenceEquals(inbox, dedup).Should().BeTrue("both facets must resolve to the same in-memory instance");
        }

        [Fact]
        public void InboxCustomBothFacetsRegisteredByConsumer_BothResolveToConsumerInstance()
        {
            using var scope = BuildScope(services =>
            {
                services.AddScoped<CustomInboxBoth>();
                services.AddScoped<IBrokeredMessageInbox>(sp => sp.GetRequiredService<CustomInboxBoth>());
                services.AddScoped<IInboxDeduplicator>(sp => sp.GetRequiredService<CustomInboxBoth>());
            });
            var sp = scope.ServiceProvider;

            var inbox = sp.GetRequiredService<IBrokeredMessageInbox>();
            var dedup = sp.GetRequiredService<IInboxDeduplicator>();

            inbox.Should().BeOfType<CustomInboxBoth>();
            ReferenceEquals(inbox, dedup).Should().BeTrue("consumer-registered pair must remain intact");
        }
    }
}
