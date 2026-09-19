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
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never, "a non-success batch with no marker-409 never triggers the confirmation read (cold-path-only)");
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

        [Fact]
        public void MustRejectReservedIdPayloadPrefixedWithUtf8ByteOrderMark()
        {
            // A UTF-8 BOM ahead of the root object must not smuggle a Reserved Item-Id Namespace id past the
            // stage-time prohibition.
            var (handle, batch) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            byte[] preamble = System.Text.Encoding.UTF8.GetPreamble();
            byte[] body = System.Text.Encoding.UTF8.GetBytes($"{{\"id\":\"{CosmosItemId.ForInbox("msg-1")}\"}}");
            var bytes = new byte[preamble.Length + body.Length];
            preamble.CopyTo(bytes, 0);
            body.CopyTo(bytes, preamble.Length);
            using var payload = new MemoryStream(bytes, writable: false);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("a leading byte-order mark is stripped before the id is peeked");
            handle.StagedOperationCount.Should().Be(0);
            batch.Verify(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Never);
        }

        [Fact]
        public void MustRejectPublicCreateWhenLastDuplicateTopLevelIdIsReserved()
        {
            // Last-id-wins on duplicate top-level keys: the earlier non-string id does not make the payload idless.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload("{\"id\":123,\"id\":\"inbox:x\"}");

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("the last top-level id decides, and it is reserved");
            handle.StagedOperationCount.Should().Be(0);
        }

        [Fact]
        public void MustAllowPublicCreateWhenLastDuplicateTopLevelIdIsNotReserved()
        {
            // The mirror of the case above: an earlier reserved-prefix duplicate is overwritten by the last one, which
            // is what Cosmos persists.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload("{\"id\":\"inbox:x\",\"id\":\"safe\"}");

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().NotThrow("the last top-level id decides, and it is not reserved");
            handle.StagedOperationCount.Should().Be(1);
        }

        [Theory]
        [InlineData("{\"id\":123}")]
        [InlineData("{\"id\":null}")]
        [InlineData("{\"id\":true}")]
        [InlineData("{\"id\":{\"v\":\"inbox:x\"}}")]
        [InlineData("{\"id\":[\"inbox:x\"]}")]
        public void MustAllowPublicCreateWhenTopLevelIdIsNotAString(string payloadText)
        {
            // Only a STRING-valued top-level id can be reserved; every other value kind is idless by treatment.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload(payloadText);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().NotThrow();
            handle.StagedOperationCount.Should().Be(1);
        }

        [Fact]
        public void MustAllowPublicCreateWhenPayloadIsMalformedAfterReservedId()
        {
            // A payload that fails to parse is idless even when a reserved-prefix id precedes the malformed tail.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload("{\"id\":\"inbox:x\",\"a\":");

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().NotThrow();
            handle.StagedOperationCount.Should().Be(1);
        }

        [Theory]
        [InlineData("{\"id\":\"inbox:x\"} junk")]
        [InlineData("{\"id\":\"inbox:x\"} 123")]
        [InlineData("{\"id\":\"inbox:x\"}{\"id\":\"safe\"}")]
        public void MustAllowPublicCreateWhenContentTrailsTheRootObject(string payloadText)
        {
            // Content past the root object makes the whole payload unparseable, hence idless — even a second
            // well-formed root value.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload(payloadText);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().NotThrow();
            handle.StagedOperationCount.Should().Be(1);
        }

        [Fact]
        public void MustRejectReservedIdWhenOnlyWhitespaceTrailsTheRootObject()
        {
            // Trailing whitespace is NOT trailing content — the payload still parses and its reserved id still binds.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload("{\"id\":\"inbox:x\"}  \r\n\t ");

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>();
            handle.StagedOperationCount.Should().Be(0);
        }

        [Theory]
        [InlineData("{\"\\u0069d\":\"inbox:x\"}")]
        [InlineData("{\"id\":\"\\u0069nbox:x\"}")]
        public void MustRejectReservedIdWrittenWithJsonEscapes(string payloadText)
        {
            // An escaped property name or an escaped value must be unescaped before the prefix test, or the
            // stage-time prohibition is trivially evaded.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload(payloadText);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("escapes must be resolved before the reserved-prefix test");
            handle.StagedOperationCount.Should().Be(0);
        }

        [Fact]
        public void MustRejectReservedIdOnNonSeekablePayload()
        {
            var (handle, batch) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = new NonSeekableStream(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"inbox:x\"}"));

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("a non-seekable payload is buffered then peeked, not waved through");
            handle.StagedOperationCount.Should().Be(0);
            batch.Verify(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Never);
        }

        [Fact]
        public void MustStageRewoundBufferForNonSeekablePayload()
        {
            // A non-seekable payload is buffered; the SDK is handed the rewound buffer, not the drained original.
            var (handle, batch) = DirectHandle();
            Stream captured = null;
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Callback<Stream, TransactionalBatchItemRequestOptions>((s, _) => captured = s)
                 .Returns(batch.Object);
            ICosmosAtomicWriteHandle publicHandle = handle;
            var json = "{\"id\":\"order:abc\",\"value\":1}";
            using var payload = new NonSeekableStream(System.Text.Encoding.UTF8.GetBytes(json));

            publicHandle.StageCreateItemStream(payload);

            captured.Should().NotBeNull().And.NotBeSameAs(payload, "the non-seekable original cannot be re-read");
            captured.CanSeek.Should().BeTrue();
            captured.Position.Should().Be(0, "the buffer is handed over rewound");
            using var reader = new StreamReader(captured);
            reader.ReadToEnd().Should().Be(json);
        }

        [Fact]
        public void MustRejectReservedIdPeekedFromNonZeroStreamPosition()
        {
            // The peek starts at the stream's CURRENT position, not at 0 — reading from 0 here would hit the
            // non-JSON prefix, parse nothing, and let the reserved id through.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            var prefix = "GARBAGE";
            using var payload = JsonPayload(prefix + "{\"id\":\"inbox:x\"}");
            payload.Position = prefix.Length;

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>();
            handle.StagedOperationCount.Should().Be(0);
        }

        [Fact]
        public void MustStageSameStreamInstanceRestoringNonZeroPosition()
        {
            var (handle, batch) = DirectHandle();
            Stream captured = null;
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Callback<Stream, TransactionalBatchItemRequestOptions>((s, _) => captured = s)
                 .Returns(batch.Object);
            ICosmosAtomicWriteHandle publicHandle = handle;
            var prefix = "GARBAGE";
            var json = "{\"id\":\"order:abc\",\"value\":1}";
            using var payload = JsonPayload(prefix + json);
            payload.Position = prefix.Length;

            publicHandle.StageCreateItemStream(payload);

            captured.Should().BeSameAs(payload, "a seekable payload is peeked in place, never copied");
            payload.Position.Should().Be(prefix.Length, "the peek restores the position the caller handed over");
            using var reader = new StreamReader(payload);
            reader.ReadToEnd().Should().Be(json);
        }

        [Fact]
        public void MustRejectReservedIdAppearingAfterALargeNestedObject()
        {
            // Teeth: the sole top-level id is reserved and sits LAST, past a quarter-megabyte of nested content. A
            // peek that gives up on a large or deeply-populated payload would let this stage.
            var builder = new System.Text.StringBuilder(320_000);
            builder.Append("{\"aggregate\":{");
            for (var field = 0; field < 8000; field++)
            {
                if (field > 0)
                {
                    builder.Append(',');
                }

                builder.Append("\"f").Append(field).Append("\":\"").Append('x', 24).Append('"');
            }

            builder.Append("},\"id\":\"inbox:trailing\"}");
            var json = builder.ToString();
            json.Length.Should().BeGreaterThan(256 * 1024, "the case only has teeth past a large payload");

            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload(json);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("a trailing top-level reserved id is still a top-level reserved id");
            handle.StagedOperationCount.Should().Be(0);
        }

        [Theory]
        [InlineData("{\"user\":{\"id\":\"inbox:x\"}}")]
        [InlineData("{\"items\":[{\"id\":\"inbox:x\"}]}")]
        [InlineData("{\"a\":{\"b\":{\"id\":\"inbox:x\"}},\"id\":\"order:abc\"}")]
        public void MustAllowReservedIdNestedBelowTheTopLevel(string payloadText)
        {
            // Teeth: only the PERSISTED item id — the top-level "id" — is in the Reserved Item-Id Namespace. An "id"
            // on a nested object or inside an array is application data and must stage untouched.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = JsonPayload(payloadText);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().NotThrow("a nested id is not the persisted item id");
            handle.StagedOperationCount.Should().Be(1);
        }

        [Fact]
        public void MustRejectReservedIdWhenStreamLengthUnderReportsThePayload()
        {
            // Teeth: the peek must judge the bytes the SDK will READ, never the byte count the stream ADVERTISES. A
            // seekable payload whose Length is shorter than its readable content would otherwise be scanned truncated,
            // parse as malformed, and be waved through as idless with a reserved id intact.
            var (handle, batch) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = new LyingLengthStream(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"inbox:x\"}"), advertisedLength: 2);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("an under-reported Length must not become an idless verdict");
            handle.StagedOperationCount.Should().Be(0);
            batch.Verify(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Never);
        }

        [Fact]
        public void MustRejectReservedIdWhenStreamLengthReportsNothingRemaining()
        {
            // The same class as above at its extreme: a Length of zero over a payload that still yields bytes.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = new LyingLengthStream(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"inbox:x\"}"), advertisedLength: 0);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("an empty-looking Length must not become an idless verdict");
            handle.StagedOperationCount.Should().Be(0);
        }

        [Fact]
        public void MustRejectReservedIdWhenStreamLengthExceedsInt32Range()
        {
            // The same class in the other direction: a Length too large to size a buffer from must fall back to
            // reading the payload, not resolve to idless without reading a byte.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = new LyingLengthStream(
                System.Text.Encoding.UTF8.GetBytes("{\"id\":\"inbox:x\"}"), advertisedLength: (long)int.MaxValue + 1);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("an unusable Length must not become an idless verdict");
            handle.StagedOperationCount.Should().Be(0);
        }

        [Fact]
        public void MustStageSafeIdWhenStreamLengthUnderReportsThePayload()
        {
            // The mirror of the rejection cases: judging the real bytes must not start REFUSING documents whose real
            // top-level id is not reserved, and the SDK must still receive the payload rewound to where it started.
            var (handle, batch) = DirectHandle();
            Stream captured = null;
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Callback<Stream, TransactionalBatchItemRequestOptions>((s, _) => captured = s)
                 .Returns(batch.Object);
            ICosmosAtomicWriteHandle publicHandle = handle;
            var json = "{\"id\":\"order:abc\",\"value\":1}";
            using var payload = new LyingLengthStream(System.Text.Encoding.UTF8.GetBytes(json), advertisedLength: 2);

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().NotThrow();
            handle.StagedOperationCount.Should().Be(1);
            captured.Should().BeSameAs(payload, "a seekable payload is peeked in place, never copied");
            captured.Position.Should().Be(0, "the peek restores the position the caller handed over");
            using var reader = new StreamReader(captured);
            reader.ReadToEnd().Should().Be(json, "the SDK must read the same bytes the peek inspected");
        }

        [Fact]
        public void MustRejectReservedIdDeliveredInSingleByteReads()
        {
            // A stream is permitted to satisfy a read request partially. A peek that treats one short read as
            // end-of-payload would scan a truncated prefix and wave the reserved id through.
            var (handle, _) = DirectHandle();
            ICosmosAtomicWriteHandle publicHandle = handle;
            using var payload = new SingleByteReadStream(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"inbox:x\"}"));

            Action act = () => publicHandle.StageCreateItemStream(payload);

            act.Should().Throw<ArgumentException>("a short read is not end-of-payload");
            handle.StagedOperationCount.Should().Be(0);
        }

        // A seekable payload stream whose Length is a LIE relative to what Read actually yields. Real consumers hand
        // over honest streams; this double exists to prove the guard derives its verdict from the bytes it reads and
        // never from the count the stream advertises.
        private sealed class LyingLengthStream : Stream
        {
            private readonly MemoryStream _inner;
            private readonly long _advertisedLength;

            public LyingLengthStream(byte[] bytes, long advertisedLength)
            {
                _inner = new MemoryStream(bytes, writable: false);
                _advertisedLength = advertisedLength;
            }

            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => _advertisedLength;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        // An honest-Length seekable payload stream that satisfies every read request one byte at a time.
        private sealed class SingleByteReadStream : Stream
        {
            private readonly MemoryStream _inner;

            public SingleByteReadStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);

            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(1, count));

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        // A read-only, forward-only payload stream: CanSeek is false and Length/Position throw, so any peek that
        // touches them without buffering first fails loudly instead of silently skipping the guard.
        private sealed class NonSeekableStream : Stream
        {
            private readonly MemoryStream _inner;

            public NonSeekableStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
