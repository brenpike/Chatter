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
        // payload (so the marker can be asserted at op index 0) and returns the configured execute response. When
        // confirmationRead is supplied it stubs Container.ReadItemStreamAsync (the cold-path duplicate-confirmation
        // point-read on the marker-409 branch) to return it; otherwise the read is left unstubbed so any test that does
        // NOT expect a confirmation read fails loudly if the behavior reads.
        private static (Mock<Container> container, Mock<TransactionalBatch> batch, List<Stream> staged) MockContainer(
            TransactionalBatchResponse executeResponse, ResponseMessage confirmationRead = null)
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
            if (confirmationRead is not null)
            {
                container.Setup(c => c.ReadItemStreamAsync(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(confirmationRead);
            }
            return (container, batch, staged);
        }

        // A successful point-read whose body is a genuine Chatter inbox marker for messageId (matches the real
        // serialized field names: "_chatterType":"inbox","MessageId":...). Confirms the duplicate -> swallow.
        private static ResponseMessage ConfirmedMarkerRead(string messageId)
            => ReadResponse(HttpStatusCode.OK, $"{{\"_chatterType\":\"inbox\",\"MessageId\":\"{messageId}\",\"ReceivedAtUtc\":\"2026-06-15T00:00:00Z\"}}");

        // A successful point-read whose body is an app document, NOT a Chatter inbox marker -> not confirmed -> redeliver.
        private static ResponseMessage NonMarkerAppDocRead()
            => ReadResponse(HttpStatusCode.OK, "{\"_chatterType\":\"order\",\"id\":\"inbox:forged\",\"value\":1}");

        // A successful point-read with the inbox discriminator forged but a DIFFERENT MessageId -> not confirmed for this
        // identity -> redeliver.
        private static ResponseMessage ForgedDiscriminatorMismatchedMessageIdRead(string expectedMessageId)
            => ReadResponse(HttpStatusCode.OK, $"{{\"_chatterType\":\"inbox\",\"MessageId\":\"{expectedMessageId}-other\"}}");

        // A NOT-FOUND point-read (TTL/delete race removed the conflicting doc) -> non-confirmable -> redeliver.
        private static ResponseMessage NotFoundRead()
            => ReadResponse(HttpStatusCode.NotFound, string.Empty);

        // A non-success point-read (e.g. transient 503) -> cannot confirm -> redeliver.
        private static ResponseMessage NonSuccessRead()
            => ReadResponse(HttpStatusCode.ServiceUnavailable, string.Empty);

        private static ResponseMessage ReadResponse(HttpStatusCode statusCode, string json)
        {
            var response = new ResponseMessage(statusCode);
            if (!string.IsNullOrEmpty(json))
            {
                response.Content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json), writable: false);
            }
            return response;
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
            // Marker op (index 0) is a 409 Conflict: a candidate duplicate. The behavior CONFIRMS it by point-reading
            // the conflicting doc, which is a genuine Chatter inbox marker for msg-1 -> confirmed duplicate. The
            // all-or-nothing batch failed (nothing committed) and the message is acked: no throw, no redeliver.
            var (container, batch, _) = MockContainer(
                FailureResponse(HttpStatusCode.Conflict, HttpStatusCode.Conflict), ConfirmedMarkerRead("msg-1"));
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().NotThrowAsync("a marker-409 confirmed against the conflicting doc is a duplicate -> ack, do not redeliver");
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once, "the marker-only batch must execute to surface the 409");
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once, "the marker-409 is confirmed by point-reading the conflicting doc");
        }

        [Fact]
        public async Task MustThrowWhenMarkerConflictConfirmationReadsNonMarkerAppDoc()
        {
            // Marker op 409, but the conflicting doc is an app document — NOT a Chatter inbox marker (the app owns the
            // container and can author a reserved-prefix id through a non-staging path). The 409 is NOT confirmed as a
            // duplicate -> throw so the colliding message's first delivery is redelivered, never silently lost.
            var (container, _, _) = MockContainer(
                FailureResponse(HttpStatusCode.Conflict, HttpStatusCode.Conflict), NonMarkerAppDocRead());
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().ThrowAsync<CosmosBatchExecutionException>(
                "an app-authored reserved-prefix collision is not a confirmed marker and must redeliver");
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once, "the marker-409 triggers a confirmation point-read");
        }

        [Fact]
        public async Task MustThrowWhenMarkerConflictConfirmationReadsForgedDiscriminatorButMismatchedMessageId()
        {
            // Marker op 409, conflicting doc forges _chatterType="inbox" but carries a DIFFERENT MessageId. Confirming
            // the discriminator alone is insufficient — the MessageId must also match the inbound identity, else it is
            // not this message's marker -> throw (redeliver).
            var (container, _, _) = MockContainer(
                FailureResponse(HttpStatusCode.Conflict, HttpStatusCode.Conflict), ForgedDiscriminatorMismatchedMessageIdRead("msg-1"));
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().ThrowAsync<CosmosBatchExecutionException>(
                "a forged inbox discriminator with a mismatched MessageId is not a confirmed marker for this identity");
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task MustThrowWhenMarkerConflictConfirmationReadIsNotFound()
        {
            // Marker op 409, but the confirmation point-read is NOT-FOUND — a TTL/delete race removed the conflicting
            // doc between the failed create and the read. The duplicate is non-confirmable -> throw (redeliver).
            var (container, _, _) = MockContainer(
                FailureResponse(HttpStatusCode.Conflict, HttpStatusCode.Conflict), NotFoundRead());
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().ThrowAsync<CosmosBatchExecutionException>("a NOT-FOUND confirmation read cannot confirm a duplicate -> redeliver");
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task MustThrowWhenMarkerConflictConfirmationReadIsNonSuccess()
        {
            // Marker op 409, but the confirmation point-read is a non-success (e.g. transient 503). The duplicate cannot
            // be confirmed -> throw (redeliver).
            var (container, _, _) = MockContainer(
                FailureResponse(HttpStatusCode.Conflict, HttpStatusCode.Conflict), NonSuccessRead());
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().ThrowAsync<CosmosBatchExecutionException>("a non-success confirmation read cannot confirm a duplicate -> redeliver");
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once);
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
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never, "the marker op is 424 not 409, so the confirmation read never runs (cold-path-only)");
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
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never, "the marker op is 424 not 409, so the confirmation read never runs (cold-path-only)");
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

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustFailLoudForParticipantWithMissingMessageIdAndStageNothing(string messageId)
        {
            // A participant that resolves a partition is on the once-only path and MUST carry an identity to dedup by. A
            // null/whitespace MessageId is a protocol/config error: the once-only guarantee cannot be honored, so the
            // behavior FAILS LOUD with InvalidOperationException BEFORE the batch is opened — nothing is staged,
            // next() never runs, nothing is acked. This matches the in-memory tier, which throws.
            var (container, batch, staged) = MockContainer(SuccessResponse());
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            var nextRan = false;
            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId(messageId), () =>
            {
                nextRan = true;
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            await act.Should().ThrowAsync<InvalidOperationException>(
                "a participant without a MessageId cannot be deduped, so the once-only guarantee fails loud before any commit");
            nextRan.Should().BeFalse("the throw precedes next()");
            container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never, "nothing is staged when the participant lacks a MessageId");
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Never, "nothing is acked when the participant lacks a MessageId");
            staged.Should().BeEmpty("no batch is opened, so no op is staged");
        }

        [Fact]
        public async Task MustSwallowMarkerOnlyBatchDuplicateWhenNoHandlerOpStaged()
        {
            // A marker-only batch (handler stages nothing) still has StagedOperationCount > 0, so it executes; a marker
            // 409 confirmed against the conflicting doc is a duplicate and is swallowed.
            var (container, batch, staged) = MockContainer(
                FailureResponse(HttpStatusCode.Conflict, HttpStatusCode.Conflict), ConfirmedMarkerRead("msg-1"));
            var registry = new DocumentReliabilityRegistry();
            registry.Add(Registration<RegisteredCommand>("shop", "orders", _ => new PartitionKey("tenant-1"), "/tenantId"));
            var factory = FactoryFor("shop", "orders", container.Object);
            var surface = new DocumentTierReliabilitySurface();
            var behavior = new DocumentTierBatchLifecycleBehavior<RegisteredCommand>(registry, factory, surface);

            Func<Task> act = () => behavior.Handle(new RegisteredCommand(), ContextWithMessageId("msg-1"), () => Task.CompletedTask);

            await act.Should().NotThrowAsync();
            staged.Should().ContainSingle("the marker is the only staged op");
            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
            container.Verify(c => c.ReadItemStreamAsync(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once, "the marker-409 is confirmed by point-reading the conflicting doc");
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
