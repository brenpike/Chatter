using Chatter.CQRS.Commands;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingDocumentTierBatchLifecycleBehavior
{
    public class WhenExecutingBatch : Testing.Core.Context
    {
        private sealed class FakeCommand : ICommand { }

        // The behavior only passes the inbound message to the resolver, which here ignores it; a mocked broker context
        // returning a null BrokeredMessage and a default CancellationToken is sufficient to drive Handle.
        private static IMessageBrokerContext MockContext()
        {
            var context = new Mock<IMessageBrokerContext>();
            context.SetupGet(c => c.BrokeredMessage).Returns((Receiving.InboundBrokeredMessage)null);
            context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
            return context.Object;
        }

        private static (DocumentContainer documentContainer, Mock<TransactionalBatch> batch, DocumentTierReliabilitySurface surface, PartitionKeyResolver resolver)
            Harness(TransactionalBatchResponse executeResponse)
        {
            var batch = new Mock<TransactionalBatch>();
            if (executeResponse is not null)
            {
                batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(executeResponse);
            }

            // The staging methods delegate to the private batch; set up CreateItemStream so the handle's
            // StageCreateItemStream call completes without null-ref on the mock's fluent returns chain.
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Returns(batch.Object);

            var container = new Mock<Container>();
            container.Setup(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>())).Returns(batch.Object);

            var resolver = new PartitionKeyResolver(_ => new PartitionKey("pk"), Array.AsReadOnly(new[] { "/tenantId" }));
            return (new DocumentContainer(container.Object), batch, new DocumentTierReliabilitySurface(), resolver);
        }

        private static TransactionalBatchResponse MockResponse(HttpStatusCode statusCode, bool isSuccess)
        {
            var response = new Mock<TransactionalBatchResponse>();
            response.SetupGet(r => r.IsSuccessStatusCode).Returns(isSuccess);
            response.SetupGet(r => r.StatusCode).Returns(statusCode);
            return response.Object;
        }

        [Fact]
        public async Task MustSkipExecuteWhenBatchIsEmpty()
        {
            var (documentContainer, batch, surface, resolver) = Harness(executeResponse: null);
            var behavior = new DocumentTierBatchLifecycleBehavior<FakeCommand>(documentContainer, resolver, surface);

            // next() stages nothing, so the empty-batch guard must skip ExecuteAsync entirely (no live Cosmos).
            await behavior.Handle(new FakeCommand(), MockContext(), () => Task.CompletedTask);

            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MustExecuteWhenAnOpIsStaged()
        {
            var (documentContainer, batch, surface, resolver) = Harness(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var behavior = new DocumentTierBatchLifecycleBehavior<FakeCommand>(documentContainer, resolver, surface);

            await behavior.Handle(new FakeCommand(), MockContext(), () =>
            {
                surface.CurrentHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustThrowWhenBatchResponseIsNotSuccess()
        {
            var (documentContainer, batch, surface, resolver) = Harness(MockResponse(HttpStatusCode.PreconditionFailed, isSuccess: false));
            var behavior = new DocumentTierBatchLifecycleBehavior<FakeCommand>(documentContainer, resolver, surface);

            // A simulated non-success batch (e.g. a 412 from a forced aggregate ETag conflict) must throw so the message
            // is NOT acked when the writes did not commit.
            Func<Task> act = () => behavior.Handle(new FakeCommand(), MockContext(), () =>
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
            var (documentContainer, batch, surface, resolver) = Harness(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var behavior = new DocumentTierBatchLifecycleBehavior<FakeCommand>(documentContainer, resolver, surface);

            ICosmosAtomicWriteHandle handleDuringNext = null;
            await behavior.Handle(new FakeCommand(), MockContext(), () =>
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
        public async Task MustCountOpAndNotExposeRawBatch()
        {
            // Regression: StageCreateItemStream must increment StagedOperationCount (staging and counting are
            // inseparable by construction — no public Batch getter or MarkOperationStaged exists on the interface).
            var (documentContainer, _, surface, resolver) = Harness(MockResponse(HttpStatusCode.OK, isSuccess: true));
            var behavior = new DocumentTierBatchLifecycleBehavior<FakeCommand>(documentContainer, resolver, surface);

            ICosmosAtomicWriteHandle capturedHandle = null;
            await behavior.Handle(new FakeCommand(), MockContext(), () =>
            {
                capturedHandle = surface.CurrentHandle;
                capturedHandle.StageCreateItemStream(Stream.Null);
                return Task.CompletedTask;
            });

            capturedHandle.StagedOperationCount.Should().Be(1);

            // The closed-by-construction contract: no public Batch getter or MarkOperationStaged member exists.
            var handleType = typeof(ICosmosAtomicWriteHandle);
            handleType.GetProperty("Batch").Should().BeNull("the raw batch must not be publicly reachable for op-adds");
            handleType.GetMethod("MarkOperationStaged").Should().BeNull("staging and counting must be one indivisible action via the Stage* methods");
        }
    }
}
