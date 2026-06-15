using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingDocumentTierBatchLifecycleBehavior
{
    public class WhenExecutingBatch : Testing.Core.Context
    {
        private sealed class RegisteredCommand : ICommand { }
        private sealed class UnregisteredCommand : ICommand { }
        private sealed class CommandA : ICommand { }
        private sealed class CommandB : ICommand { }

        // A broker context whose inbound message is NULL — the in-process (non-broker-receive) case. Used to drive the
        // null-inbound bare-pass-through path: a participant command with no inbound message must bypass the resolver
        // and open no batch.
        private static IMessageBrokerContext MockContextWithNullInboundMessage()
        {
            var context = new Mock<IMessageBrokerContext>();
            context.SetupGet(c => c.BrokeredMessage).Returns((Receiving.InboundBrokeredMessage)null);
            context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
            return context.Object;
        }

        // A broker context carrying a NON-null inbound brokered message — the broker-receive case. A real
        // MessageBrokerContext (public ctor) builds a non-null BrokeredMessage internally, so the behavior's
        // null-inbound bare-pass-through guard is NOT triggered and a registered command proceeds to the resolver and
        // batch. The resolvers in these tests ignore the inbound message; the non-null value only satisfies the guard.
        private static IMessageBrokerContext MockContext()
            => new MessageBrokerContext("msg-1", Array.Empty<byte>(), null, "receiver", CancellationToken.None, new JsonBodyConverter());

        // Add is internal; visible to the test assembly via InternalsVisibleTo.
        private static void Register(DocumentReliabilityRegistry registry, DocumentReliabilityRegistration registration)
            => registry.Add(registration);

        private static TransactionalBatchResponse MockResponse(HttpStatusCode statusCode, bool isSuccess)
        {
            var response = new Mock<TransactionalBatchResponse>();
            response.SetupGet(r => r.IsSuccessStatusCode).Returns(isSuccess);
            response.SetupGet(r => r.StatusCode).Returns(statusCode);
            return response.Object;
        }

        // Builds a container mock whose CreateTransactionalBatch returns a configurable batch mock.
        private static (Mock<Container> container, Mock<TransactionalBatch> batch) MockContainer(TransactionalBatchResponse executeResponse)
        {
            var batch = new Mock<TransactionalBatch>();
            if (executeResponse is not null)
            {
                batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(executeResponse);
            }
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Returns(batch.Object);

            var container = new Mock<Container>();
            container.Setup(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>())).Returns(batch.Object);
            return (container, batch);
        }

        private static CosmosContainerFactory FactoryFor(string database, string container, Container resolved)
        {
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer(database, container)).Returns(resolved);
            var services = new ServiceCollection();
            services.AddSingleton(client.Object);
            return new CosmosContainerFactory(services.BuildServiceProvider());
        }

        private static DocumentReliabilityRegistration Registration<TCommand>(string database, string container, ResolvePartitionKey resolver = null)
            where TCommand : ICommand
            => new DocumentReliabilityRegistration(
                typeof(TCommand),
                database,
                container,
                container + "-leases",
                resolver ?? (_ => new PartitionKey("pk")),
                Array.AsReadOnly(new[] { "/tenantId" }));

        [Fact]
        public async Task MustOpenBatchOnRegistrationsContainerAndInvokeResolverForRegisteredCommand()
        {
            var resolverInvoked = false;
            var (container, batch) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var registry = new DocumentReliabilityRegistry();
            Register(registry, Registration<RegisteredCommand>("shop", "orders", resolver: _ =>
            {
                resolverInvoked = true;
                return new PartitionKey("pk");
            }));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            await behavior.Handle(new RegisteredCommand(), MockContext(), () =>
            {
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            resolverInvoked.Should().BeTrue();
            container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Once);
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustBarePassThroughForUnregisteredCommandWithNoResolverNoBatchNoExecute()
        {
            var resolverInvoked = false;
            var (container, batch) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var registry = new DocumentReliabilityRegistry();
            // Only RegisteredCommand participates; UnregisteredCommand must bypass entirely.
            Register(registry, Registration<RegisteredCommand>("shop", "orders", resolver: _ =>
            {
                resolverInvoked = true;
                return new PartitionKey("pk");
            }));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<UnregisteredCommand>(registry, factory, surface);

            var nextRan = false;
            await behavior.Handle(new UnregisteredCommand(), MockContext(), () =>
            {
                nextRan = true;
                return Task.CompletedTask;
            });

            nextRan.Should().BeTrue();
            resolverInvoked.Should().BeFalse("a non-participant never reaches the resolver");
            container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Never);
            surface.CurrentHandle.Should().BeNull();
        }

        [Fact]
        public async Task MustBarePassThroughWhenParticipantResolverReturnsNullPartitionKey()
        {
            var (container, batch) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var registry = new DocumentReliabilityRegistry();
            // Participant whose resolver returns null = "no resolvable partition for this message".
            Register(registry, Registration<RegisteredCommand>("shop", "orders", resolver: _ => null));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            var nextRan = false;
            await behavior.Handle(new RegisteredCommand(), MockContext(), () =>
            {
                nextRan = true;
                return Task.CompletedTask;
            });

            nextRan.Should().BeTrue();
            container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Never);
            surface.CurrentHandle.Should().BeNull();
        }

        [Fact]
        public async Task MustBarePassThroughForRegisteredCommandWhenInboundMessageIsNull()
        {
            // Regression: a REGISTERED (participant) command handled OUTSIDE a broker-receive context has a null inbound
            // message (GetInboundBrokeredMessage returns null for in-process commands). The behavior must bare-pass-through
            // BEFORE invoking the resolver — a resolver that dereferences the inbound message must NEVER be reached, so the
            // null-inbound NRE class is unrepresentable regardless of resolver implementation.
            var (container, batch) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var registry = new DocumentReliabilityRegistry();
            // A resolver that dereferences the inbound message: it would throw NullReferenceException if ever called with
            // a null inbound message. The guard must prevent the call entirely.
            Register(registry, Registration<RegisteredCommand>("shop", "orders",
                resolver: inbound => new PartitionKey(inbound.MessageId)));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            var nextRan = false;
            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), MockContextWithNullInboundMessage(), () =>
            {
                nextRan = true;
                return Task.CompletedTask;
            });

            await act.Should().NotThrowAsync("the null-inbound guard bare-passes-through before the dereferencing resolver runs");
            nextRan.Should().BeTrue();
            container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Never);
            surface.CurrentHandle.Should().BeNull();
        }

        [Fact]
        public async Task MustSkipExecuteWhenBatchIsEmpty()
        {
            var (container, batch) = MockContainer(executeResponse: null);
            var registry = new DocumentReliabilityRegistry();
            // A participant whose resolver returns a null partition key opens NO batch, so there is no op to commit and
            // ExecuteAsync must never run (no live Cosmos). A participant with a resolved partition now always stamps a
            // marker as op 0, so the batch is never empty on that path — the no-op-to-commit case is the null-partition
            // bare-pass-through.
            Register(registry, Registration<RegisteredCommand>("shop", "orders", resolver: _ => null));
            var factory = FactoryFor("shop", "orders", container.Object);
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, new DocumentTierReliabilitySurface());

            await behavior.Handle(new RegisteredCommand(), MockContext(), () => Task.CompletedTask);

            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MustExecuteWhenAnOpIsStaged()
        {
            var (container, batch) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var registry = new DocumentReliabilityRegistry();
            Register(registry, Registration<RegisteredCommand>("shop", "orders"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            await behavior.Handle(new RegisteredCommand(), MockContext(), () =>
            {
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustThrowWhenBatchResponseIsNotSuccess()
        {
            var (container, _) = MockContainer(MockResponse(HttpStatusCode.PreconditionFailed, isSuccess: false));
            var registry = new DocumentReliabilityRegistry();
            Register(registry, Registration<RegisteredCommand>("shop", "orders"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            // A simulated non-success batch (e.g. a 412 from a forced aggregate ETag conflict) must throw so the message
            // is NOT acked when the writes did not commit.
            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), MockContext(), () =>
            {
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            (await act.Should().ThrowAsync<CosmosBatchExecutionException>())
                .Which.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        }

        [Fact]
        public async Task MustExposeHandleOnSurfaceDuringNextAndClearAfter()
        {
            var (container, _) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var registry = new DocumentReliabilityRegistry();
            Register(registry, Registration<RegisteredCommand>("shop", "orders"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            ICosmosAtomicWriteHandle handleDuringNext = null;
            await behavior.Handle(new RegisteredCommand(), MockContext(), () =>
            {
                handleDuringNext = surface.CurrentHandle;
                handleDuringNext.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            handleDuringNext.Should().NotBeNull();
            handleDuringNext.PartitionKeyPath.Should().ContainSingle().Which.Should().Be("/tenantId");
            surface.CurrentHandle.Should().BeNull();
        }

        [Fact]
        public async Task MustSelectPerCommandContainerWithNoCrossWiring()
        {
            var (containerMockA, _) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var (containerMockB, _) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var containerA = containerMockA.Object;
            var containerB = containerMockB.Object;

            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer("dbA", "containerA")).Returns(containerA);
            client.Setup(c => c.GetContainer("dbB", "containerB")).Returns(containerB);
            var services = new ServiceCollection();
            services.AddSingleton(client.Object);
            var factory = new CosmosContainerFactory(services.BuildServiceProvider());

            var registry = new DocumentReliabilityRegistry();
            Register(registry, Registration<CommandA>("dbA", "containerA"));
            Register(registry, Registration<CommandB>("dbB", "containerB"));

            var surface = new DocumentTierReliabilitySurface();
            var behaviorA = new DocumentTierBatchLifecycleBehavior<CommandA>(registry, factory, surface);
            var behaviorB = new DocumentTierBatchLifecycleBehavior<CommandB>(registry, factory, surface);

            // A valid MessageId stamps the inbox marker as op 0 on each batch; this test asserts container selection
            // only — each command opens its batch on ITS OWN registration's container, no cross-wiring.
            await behaviorA.Handle(new CommandA(), MockContext(), () => Task.CompletedTask);
            await behaviorB.Handle(new CommandB(), MockContext(), () => Task.CompletedTask);

            containerMockA.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Once);
            containerMockB.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Once);
        }

        [Fact]
        public async Task MustCountOpAndNotExposeRawBatch()
        {
            // Regression: StageCreateItemStream must increment StagedOperationCount (staging and counting are
            // inseparable by construction — no public Batch getter or MarkOperationStaged exists on the interface).
            var (container, _) = MockContainer(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var registry = new DocumentReliabilityRegistry();
            Register(registry, Registration<RegisteredCommand>("shop", "orders"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            // A valid MessageId stamps the inbox marker as op 0, then the handler stages one op — so the count reflects
            // both: the marker plus the handler's closed-by-construction Stage* increment under test.
            ICosmosAtomicWriteHandle capturedHandle = null;
            await behavior.Handle(new RegisteredCommand(), MockContext(), () =>
            {
                capturedHandle = surface.CurrentHandle;
                capturedHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            capturedHandle.StagedOperationCount.Should().Be(2, "the inbox marker (op 0) plus the handler's staged op");

            // The closed-by-construction contract: no public Batch getter or MarkOperationStaged member exists.
            var handleType = typeof(ICosmosAtomicWriteHandle);
            handleType.GetProperty("Batch").Should().BeNull("the raw batch must not be publicly reachable for op-adds");
            handleType.GetMethod("MarkOperationStaged").Should().BeNull("staging and counting must be one indivisible action via the Stage* methods");
        }

        // A direct handle over a batch mock that records each CreateItemStream op, for the reserved-namespace guard tests
        // (no behavior/pipeline needed — the guard lives on the handle's public staging methods).
        private static (CosmosAtomicWriteHandle handle, Mock<TransactionalBatch> batch) DirectHandle()
        {
            var batch = new Mock<TransactionalBatch>();
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Returns(batch.Object);
            var handle = new CosmosAtomicWriteHandle(
                Mock.Of<Container>(), batch.Object, new PartitionKey("pk"), Array.AsReadOnly(new[] { "/tenantId" }));
            return (handle, batch);
        }

        private static MemoryStream JsonPayload(string json)
            => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json), writable: false);

        // Iter-2 closure: an app create whose ITEM serializes to a reserved-prefix id must be rejected on the sole
        // surviving public create/upsert path (StageCreateItemStream), whose guard keys on the PERSISTED item.id peeked
        // from the payload bytes the SDK reads — not on a separate parameter that could diverge from what Cosmos
        // persists. The removed typed StageCreateItem<T>/StageUpsertItem<T> guarded an explicit `id` PARAMETER that the
        // SDK ignored (it derives the persisted id from item.id), so a caller could pass id:"safe" while the item
        // serialized to {"id":"inbox:..."} and stage a reserved-prefix doc unguarded — silent first-delivery-loss.
        [Fact]
        public void MustRejectPublicCreateWhenItemSerializesToReservedId()
        {
            var (handle, batch) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload($"{{\"id\":\"{CosmosItemId.ForInbox("msg-1")}\"}}");

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("the peek-guard rejects a payload whose persisted item.id is reserved-prefix");
            handle.StagedOperationCount.Should().Be(0);
            batch.Verify(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Never);
        }

        [Fact]
        public void MustAllowPublicCreateItemStreamWhenPayloadIdIsNonReserved()
        {
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload("{\"id\":\"order:abc\",\"value\":1}");

            publicHandle.StageCreateItemStream(payload);

            handle.StagedOperationCount.Should().Be(1, "a non-reserved app id passes the peek-guard and stages");
        }

        [Theory]
        [InlineData("")]
        [InlineData("{}")]
        [InlineData("[1,2,3]")]
        [InlineData("\"just-a-string\"")]
        [InlineData("not-json-at-all")]
        public void MustAllowPublicCreateItemStreamForIdlessOrUnparseablePayload(string payloadText)
        {
            // An empty, non-object, or unparseable payload is idless (non-reserved) and must pass — the peek must not
            // throw on these shapes.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload(payloadText);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().NotThrow();
            handle.StagedOperationCount.Should().Be(1);
        }

        [Fact]
        public void MustAllowPublicCreateItemStreamForStreamNull()
        {
            // Stream.Null is empty -> idless -> non-reserved -> passes (the existing batch-mechanics tests rely on this).
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;

            Action act = () => publicHandle.StageCreateItemStream(Stream.Null);

            act.Should().NotThrow();
            handle.StagedOperationCount.Should().Be(1);
        }

        [Fact]
        public void MustReadSamePayloadBytesAfterPeekRestoringStreamPosition()
        {
            // The peek must not corrupt the stream the SDK later reads: capture the staged stream and confirm it still
            // yields the full original payload from position 0.
            var (handle, batch) = DirectHandle();
            Stream captured = null;
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Callback<Stream, TransactionalBatchItemRequestOptions>((s, _) => captured = s)
                 .Returns(batch.Object);
            ICosmosAtomicWriteHandle publicHandle = handle;
            var json = "{\"id\":\"order:abc\",\"value\":1}";
            using var payload = JsonPayload(json);

            publicHandle.StageCreateItemStream(payload);

            captured.Should().NotBeNull();
            captured.Position = 0;
            using var reader = new StreamReader(captured);
            reader.ReadToEnd().Should().Be(json, "the SDK must read the same bytes the peek inspected");
        }
    }
}
