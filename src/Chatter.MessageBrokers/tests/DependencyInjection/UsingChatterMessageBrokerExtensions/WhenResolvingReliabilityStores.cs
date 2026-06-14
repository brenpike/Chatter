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
    /// Verifies the cast-at-consumption reliability model: <see cref="IPollableOutboxStore"/> and
    /// <see cref="IInboxDeduplicator"/> are NOT independent DI registrations. Each is obtained by
    /// casting the single resolved primary (<see cref="IBrokeredMessageOutbox"/> /
    /// <see cref="IBrokeredMessageInbox"/>) at the consumption site, exactly as
    /// <see cref="OutboxProcessor"/> casts to <see cref="IUnitOfWork"/>. Split-store is impossible
    /// by construction: there is exactly one resolved instance per pair.
    ///
    /// INVARIANT: all cases resolve facets within a single <see cref="IServiceScope"/> — cross-scope
    /// resolution would give different scoped instances by definition and is not a split-store defect.
    /// </summary>
    public class WhenResolvingReliabilityStores : Testing.Core.Context
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

        // Implements ONLY IBrokeredMessageOutbox — casting to IPollableOutboxStore must throw.
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

        // Implements ONLY IBrokeredMessageInbox — casting to IInboxDeduplicator must throw.
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

        // ------------------------------------------------------------------ outbox: default in-memory

        [Fact]
        public void OutboxDefault_ResolvesIBrokeredMessageOutboxToInMemoryInstance()
        {
            using var scope = BuildScope();
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();

            outbox.Should().BeOfType<InMemoryBrokeredMessageOutbox>();
        }

        [Fact]
        public void OutboxDefault_CastingToPollableYieldsSameInstance()
        {
            using var scope = BuildScope();
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();
            var pollable = (IPollableOutboxStore)outbox;

            ReferenceEquals(outbox, pollable).Should().BeTrue("cast must return the same instance, not a wrapper");
        }

        [Fact]
        public void OutboxDefault_NoPollableOutboxStoreDescriptorRegistered()
        {
            using var scope = BuildScope();

            // IPollableOutboxStore is NOT a DI service — it is obtained by casting the primary.
            var pollable = scope.ServiceProvider.GetService<IPollableOutboxStore>();
            pollable.Should().BeNull("IPollableOutboxStore must not be an independent DI registration");
        }

        // ------------------------------------------------------------------ outbox: custom primary implementing both

        [Fact]
        public void OutboxCustomPrimaryImplementingBoth_ResolvesToCustomInstance()
        {
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageOutbox, CustomOutboxBoth>());
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();

            outbox.Should().BeOfType<CustomOutboxBoth>();
        }

        [Fact]
        public void OutboxCustomPrimaryImplementingBoth_CastToPollableYieldsSameInstance()
        {
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageOutbox, CustomOutboxBoth>());
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();
            var pollable = (IPollableOutboxStore)outbox;

            ReferenceEquals(outbox, pollable).Should().BeTrue("cast must return the same custom instance");
        }

        // ------------------------------------------------------------------ outbox: custom primary-only (cast guard)

        [Fact]
        public void OutboxCustomPrimaryOnly_CastToPollableThrowsInvalidCastException()
        {
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageOutbox, CustomOutboxPrimaryOnly>());
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();
            Action cast = () => { var _ = (IPollableOutboxStore)outbox; };

            cast.Should().Throw<InvalidCastException>("a custom primary that does not implement IPollableOutboxStore must throw at the cast site");
        }

        // ------------------------------------------------------------------ outbox: last-wins / duplicate-primary

        [Fact]
        public void OutboxDuplicatePrimary_LastWinsAndCastYieldsSameInstance()
        {
            // Register a custom primary before AddMessageBrokers. AddIfNotRegistered leaves the custom
            // one in place (last-wins by descriptor order). Resolving IBrokeredMessageOutbox yields
            // exactly the custom instance; its cast to IPollableOutboxStore must be the same object.
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageOutbox, CustomOutboxBoth>());
            var sp = scope.ServiceProvider;

            var outbox = sp.GetRequiredService<IBrokeredMessageOutbox>();
            var pollable = (IPollableOutboxStore)outbox;

            outbox.Should().BeOfType<CustomOutboxBoth>("custom primary wins when registered before AddMessageBrokers");
            ReferenceEquals(outbox, pollable).Should().BeTrue("one instance — split unrepresentable");
        }

        // ------------------------------------------------------------------ inbox: default in-memory

        [Fact]
        public void InboxDefault_ResolvesIBrokeredMessageInboxToInMemoryInstance()
        {
            using var scope = BuildScope();
            var sp = scope.ServiceProvider;

            var inbox = sp.GetRequiredService<IBrokeredMessageInbox>();

            inbox.Should().BeOfType<InMemoryBrokeredMessageInbox>();
        }

        [Fact]
        public void InboxDefault_NoInboxDeduplicatorDescriptorRegistered()
        {
            using var scope = BuildScope();

            // IInboxDeduplicator is NOT a DI service — it is obtained by casting the primary.
            var dedup = scope.ServiceProvider.GetService<IInboxDeduplicator>();
            dedup.Should().BeNull("IInboxDeduplicator must not be an independent DI registration");
        }

        // ------------------------------------------------------------------ inbox: last-wins / duplicate-primary

        [Fact]
        public void InboxDuplicatePrimary_LastWinsAndCastYieldsSameInstance()
        {
            // Register a custom primary before AddMessageBrokers. AddIfNotRegistered leaves the custom
            // one in place. Resolving IBrokeredMessageInbox yields the custom instance; cast must match.
            using var scope = BuildScope(services =>
                services.AddScoped<IBrokeredMessageInbox, CustomInboxBoth>());
            var sp = scope.ServiceProvider;

            var inbox = sp.GetRequiredService<IBrokeredMessageInbox>();
            var dedup = (IInboxDeduplicator)inbox;

            inbox.Should().BeOfType<CustomInboxBoth>("custom primary wins when registered before AddMessageBrokers");
            ReferenceEquals(inbox, dedup).Should().BeTrue("one instance — split unrepresentable");
        }
    }
}
