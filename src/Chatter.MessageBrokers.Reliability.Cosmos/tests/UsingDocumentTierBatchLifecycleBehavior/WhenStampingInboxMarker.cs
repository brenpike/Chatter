using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingDocumentTierBatchLifecycleBehavior
{
    public class WhenStampingInboxMarker : Testing.Core.Context
    {
        private sealed class RegisteredCommand : ICommand { }
        private sealed class UnregisteredCommand : ICommand { }

        // The marker is staged FIRST, so it is always batch op index 0. This const mirrors the behavior's own constant
        // so a future reorder is caught by the assertions below.
        private const int MarkerOperationIndex = 0;

        private static IMessageBrokerContext ContextWithMessageId(string messageId)
            => new MessageBrokerContext(messageId, Array.Empty<byte>(), null, "receiver", CancellationToken.None, new JsonBodyConverter());

        // A success per-op + batch response.
        private static TransactionalBatchResponse SuccessResponse()
        {
            var response = new Mock<TransactionalBatchResponse>();
            response.SetupGet(r => r.IsSuccessStatusCode).Returns(true);
            response.SetupGet(r => r.StatusCode).Returns(HttpStatusCode.OK);
            return response.Object;
        }

        // A non-success batch response with per-op results: index i carries opStatusCodes[i].
        private static TransactionalBatchResponse FailureResponse(HttpStatusCode batchStatus, params HttpStatusCode[] opStatusCodes)
        {
            var response = new Mock<TransactionalBatchResponse>();
            response.SetupGet(r => r.IsSuccessStatusCode).Returns(false);
            response.SetupGet(r => r.StatusCode).Returns(batchStatus);
            response.SetupGet(r => r.Count).Returns(opStatusCodes.Length);
            for (var i = 0; i < opStatusCodes.Length; i++)
            {
                var opResult = new Mock<TransactionalBatchOperationResult>();
                opResult.SetupGet(o => o.StatusCode).Returns(opStatusCodes[i]);
                response.Setup(r => r[i]).Returns(opResult.Object);
            }
            return response.Object;
        }

        // Builds a container whose CreateTransactionalBatch returns a batch that captures each staged CreateItemStream
        // payload (so the marker can be asserted at op index 0) and returns the configured execute response.
        private static (Mock<Container> container, Mock<TransactionalBatch> batch, List<Stream> staged) MockContainer(TransactionalBatchResponse executeResponse)
        {
            var staged = new List<Stream>();
            var batch = new Mock<TransactionalBatch>();
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Callback<Stream, TransactionalBatchItemRequestOptions>((stream, _) => staged.Add(stream))
                 .Returns(batch.Object);
            if (executeResponse is not null)
            {
                batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(executeResponse);
            }

            var container = new Mock<Container>();
            container.Setup(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>())).Returns(batch.Object);
            return (container, batch, staged);
        }

        private static CosmosContainerFactory FactoryFor(string database, string container, Container resolved)
        {
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer(database, container)).Returns(resolved);
            var services = new ServiceCollection();
            services.AddSingleton(client.Object);
            return new CosmosContainerFactory(services.BuildServiceProvider());
        }

        private static DocumentReliabilityRegistration Registration<TCommand>(string database, string container, ResolvePartitionKey resolver, params string[] partitionKeyPath)
            where TCommand : ICommand
            => new DocumentReliabilityRegistration(
                typeof(TCommand),
                database,
                container,
                container + "-leases",
                resolver,
                Array.AsReadOnly(partitionKeyPath));

        private static JsonElement ReadStagedDocument(Stream stream)
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        [Fact]
        public async Task MustStageMarkerAsOpIndexZeroOnTheSameBatchForParticipantWithMessageId()
        {
            var (container, batch, staged) = MockContainer(SuccessResponse());
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            // The handler also stages an op, so the batch carries [marker, handler-op]; the marker must be index 0.
            await behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () =>
            {
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            // One batch, one execute — the marker rides the SAME framework-owned batch as the handler op (atomic).
            container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Once);
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);

            staged.Should().HaveCountGreaterThanOrEqualTo(1);
            var markerDocument = ReadStagedDocument(staged[MarkerOperationIndex]);
            markerDocument.GetProperty("_chatterType").GetString().Should().Be("inbox");
            markerDocument.GetProperty("id").GetString().Should().Be(CosmosItemId.ForInbox("msg-1"));
            markerDocument.GetProperty("MessageId").GetString().Should().Be("msg-1");
            markerDocument.GetProperty("tenantId").GetString().Should().Be("tenant-1");
        }

        [Fact]
        public async Task MustSwallowMarkerOpConflictAsConfirmedDuplicateWithoutThrowing()
        {
            // Marker op (index 0) is a 409 Conflict: the marker already exists in this partition -> confirmed duplicate.
            // The all-or-nothing batch failed (nothing committed) and the message is acked: no throw, no redeliver.
            var (container, batch, _) = MockContainer(FailureResponse(HttpStatusCode.Conflict, HttpStatusCode.Conflict));
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().NotThrowAsync("a 409 on the marker op is a confirmed duplicate -> ack, do not redeliver");
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once, "the marker-only batch must execute to surface the 409");
        }

        [Fact]
        public async Task MustThrowOnAggregateConcurrencyConflictNotSwallowAsDuplicate()
        {
            // Aggregate ETag/412: marker op (index 0) is 424 failed-dependency because the aggregate op (index 1) is a
            // 412 PreconditionFailed. This is a concurrency conflict, NOT a duplicate -> throw so the app retries.
            // Conflating this with a duplicate would be silent message loss.
            var (container, _, _) = MockContainer(FailureResponse(
                HttpStatusCode.PreconditionFailed,
                (HttpStatusCode)424,
                HttpStatusCode.PreconditionFailed));
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () =>
            {
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            (await act.Should().ThrowAsync<CosmosBatchExecutionException>())
                .Which.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        }

        [Fact]
        public async Task MustThrowWhenNonMarkerOpConflictsEvenThoughBatchStatusIsConflict()
        {
            // A 409 on a NON-marker op (index 1) while the marker op (index 0) did not conflict is NOT a duplicate ->
            // throw. Only the MARKER op's own 409 is the duplicate signal; a batch-level 409 alone must not be swallowed.
            var (container, _, _) = MockContainer(FailureResponse(
                HttpStatusCode.Conflict,
                (HttpStatusCode)424,
                HttpStatusCode.Conflict));
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () =>
            {
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            await act.Should().ThrowAsync<CosmosBatchExecutionException>("an aggregate-op 409 is not the marker-op 409 and must redeliver");
        }

        [Fact]
        public async Task MustThrowOnOtherNonSuccessFailure()
        {
            // Any other non-success (e.g. a 503 service unavailable, no useful per-op marker conflict) must throw.
            var (container, _, _) = MockContainer(FailureResponse(
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable));
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().ThrowAsync<CosmosBatchExecutionException>();
        }

        [Fact]
        public async Task MustNotStampMarkerOrOpenBatchForNonParticipant()
        {
            var (container, batch, staged) = MockContainer(SuccessResponse());
            var registry = new DocumentReliabilityRegistry();
            // Only RegisteredCommand participates; UnregisteredCommand bypasses entirely.
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<UnregisteredCommand>(registry, factory, surface);

            var nextRan = false;
            await behavior.Handle(new UnregisteredCommand(), ContextWithMessageId("msg-1"), () =>
            {
                nextRan = true;
                return Task.CompletedTask;
            });

            nextRan.Should().BeTrue();
            container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
            staged.Should().BeEmpty("a non-participant stamps no marker");
        }

        [Fact]
        public async Task MustNotStampMarkerButStillRunBatchAndNextForParticipantWithWhitespaceMessageId()
        {
            // A participant whose inbound message has a whitespace MessageId cannot be deduped by identity, so NO marker
            // is stamped — but the batch is still opened and next() still runs so the aggregate/outbox path is
            // unaffected (parity with relational ReceiveViaInbox null-id behavior + #238 null-safety).
            var (container, batch, staged) = MockContainer(SuccessResponse());
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            var nextRan = false;
            await behavior.Handle(new RegisteredCommand(), ContextWithMessageId("   "), () =>
            {
                nextRan = true;
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            nextRan.Should().BeTrue();
            // The batch was opened (handler op present) and executed, but only the handler op was staged — no inbox
            // marker for a whitespace MessageId. The handler staged Stream.Null, so exactly one op is present.
            container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Once);
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
            staged.Should().ContainSingle("only the handler op was staged; no inbox marker for a whitespace MessageId");
        }

        [Fact]
        public async Task MustSwallowMarkerOnlyBatchDuplicateWhenNoHandlerOpStaged()
        {
            // A marker-only batch (handler stages nothing) still has StagedOperationCount > 0, so it executes; a marker
            // 409 is a confirmed duplicate and is swallowed.
            var (container, batch, staged) = MockContainer(FailureResponse(HttpStatusCode.Conflict, HttpStatusCode.Conflict));
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().NotThrowAsync();
            staged.Should().ContainSingle("the marker is the only staged op");
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustStampMarkerWithHierarchicalPartitionKeyAtNestedPaths()
        {
            var partitionKey = new PartitionKeyBuilder().Add("acme").Add("us-east").Build();
            var (container, _, staged) = MockContainer(SuccessResponse());
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => partitionKey, "/tenant/id", "/region"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            await behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            var markerDocument = ReadStagedDocument(staged[MarkerOperationIndex]);
            markerDocument.GetProperty("_chatterType").GetString().Should().Be("inbox");
            markerDocument.GetProperty("tenant").GetProperty("id").GetString().Should().Be("acme");
            markerDocument.GetProperty("region").GetString().Should().Be("us-east");
        }
    }
}
