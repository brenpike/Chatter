using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingMonitoredContainerContract
{
    public class WhenVerifyingTheMonitoredContainer : Testing.Core.Context
    {
        private const string DatabaseId = "shop";
        private const string ContainerId = "orders";

        // A monitored container whose ReadContainerAsync reports the supplied ground-truth partition-key path and
        // default time-to-live. ContainerProperties exposes PartitionKeyPaths only through its constructors, and
        // ContainerResponse ships a mocking constructor, so both sides of the read are real SDK types.
        private static Mock<Container> MonitoredContainer(IReadOnlyList<string> actualPartitionKeyPaths, int? defaultTimeToLive)
        {
            var properties = new ContainerProperties(ContainerId, actualPartitionKeyPaths)
            {
                DefaultTimeToLive = defaultTimeToLive,
            };

            var response = new Mock<ContainerResponse>();
            response.SetupGet(r => r.Resource).Returns(properties);

            return ContainerReading(() => Task.FromResult(response.Object));
        }

        private static Mock<Container> ContainerReading(Func<Task<ContainerResponse>> read)
        {
            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns(DatabaseId);

            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns(ContainerId);
            container.SetupGet(c => c.Database).Returns(database.Object);
            container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Returns(() => read());
            return container;
        }

        private static IReadOnlyList<string> Path(params string[] segments) => Array.AsReadOnly(segments);

        [Fact]
        public async Task MustAcceptAContainerWhosePartitionKeyPathMatchesExactly()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId"), CancellationToken.None);

            await verify.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustRejectAPartitionKeyPathThatDiffersOnlyByCase()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/TenantId"), CancellationToken.None);

            (await verify.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("/TenantId").And.Contain("/tenantId");
        }

        [Fact]
        public async Task MustReadTheContainerPropertiesExactlyOncePerVerification()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId"), defaultTimeToLive: -1);

            await MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId"), CancellationToken.None);

            container.Verify(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustAcceptADeclaredPathWrittenWithoutItsLeadingSlash()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("tenantId"), CancellationToken.None);

            await verify.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustRejectAHierarchicalPathDeclaredInTheWrongOrder()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId", "/region"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/region", "/tenantId"), CancellationToken.None);

            (await verify.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("/region, /tenantId").And.Contain("/tenantId, /region");
        }

        [Fact]
        public async Task MustRejectAHierarchicalPathDeclaredWithTheWrongSegmentCount()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId", "/region"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId"), CancellationToken.None);

            await verify.Should().ThrowAsync<InvalidOperationException>();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(null)]
        public async Task MustAcceptAContainerWhoseDefaultTimeToLiveCannotPurgeAPendingDocument(int? defaultTimeToLive)
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId"), defaultTimeToLive);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId"), CancellationToken.None);

            await verify.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustRejectAPositiveDefaultTimeToLiveNamingTheConfiguredValue()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId"), defaultTimeToLive: 86400);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId"), CancellationToken.None);

            (await verify.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("86400");
        }

        [Fact]
        public async Task MustRejectADefaultTimeToLiveOfZero()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId"), defaultTimeToLive: 0);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId"), CancellationToken.None);

            await verify.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustReportBothViolationsInOneThrowWhenThePathAndTheTimeToLiveAreBothWrong()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId"), defaultTimeToLive: 86400);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/TenantId"), CancellationToken.None);

            (await verify.Should().ThrowExactlyAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("/TenantId").And.Contain("/tenantId").And.Contain("86400");
        }

        [Fact]
        public async Task MustSurfaceAFailedPropertiesReadAsAStartupFailureCarryingTheCosmosException()
        {
            var readFailure = new CosmosException("Forbidden", HttpStatusCode.Forbidden, subStatusCode: 0, activityId: "activity-1", requestCharge: 0);
            Mock<Container> container = ContainerReading(() => Task.FromException<ContainerResponse>(readFailure));

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId"), CancellationToken.None);

            InvalidOperationException thrown = (await verify.Should().ThrowAsync<InvalidOperationException>()).Which;
            thrown.InnerException.Should().BeSameAs(readFailure);
            thrown.Message.Should().Contain(ContainerId).And.Contain(DatabaseId);
        }

        [Fact]
        public async Task MustRejectAContainerPartitionedOnTheTimeToLivePathTheDeliveredStampPatches()
        {
            Mock<Container> container = MonitoredContainer(Path("/ttl"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/ttl"), CancellationToken.None);

            (await verify.Should().ThrowAsync<InvalidOperationException>(
                "the delivered stamp patches /ttl on every drain and Cosmos rejects a patch of the partition key, so no document could ever be stamped delivered"))
                .Which.Message.Should().Contain("/ttl");
        }

        [Fact]
        public async Task MustRejectAContainerPartitionedOnTheStatusPathTheStampPatches()
        {
            Mock<Container> container = MonitoredContainer(Path("/status"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/status"), CancellationToken.None);

            (await verify.Should().ThrowAsync<InvalidOperationException>(
                "the status stamp patches /status on every drain, so a container partitioned on it can never advance a document out of pending"))
                .Which.Message.Should().Contain("/status");
        }

        [Fact]
        public async Task MustRejectAHierarchicalPartitionKeyCarryingAStampedPathInAnyPosition()
        {
            Mock<Container> container = MonitoredContainer(Path("/tenantId", "/ttl"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId", "/ttl"), CancellationToken.None);

            (await verify.Should().ThrowAsync<InvalidOperationException>(
                "a stamped path anywhere in a hierarchical partition key is still part of the partition key Cosmos refuses to patch"))
                .Which.Message.Should().Contain("/ttl");
        }

        [Fact]
        public async Task MustAcceptAPartitionKeyPathThatOnlyResemblesAStampedPathInCase()
        {
            Mock<Container> container = MonitoredContainer(Path("/Ttl"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/Ttl"), CancellationToken.None);

            await verify.Should().NotThrowAsync("Cosmos partition-key paths are case-sensitive, so /Ttl is a different field from the /ttl the relay patches");
        }

        [Fact]
        public async Task MustReportBothViolationsInOneThrowWhenThePathIsMismatchedAndAlsoCollidesWithAStampedPath()
        {
            Mock<Container> container = MonitoredContainer(Path("/ttl"), defaultTimeToLive: -1);

            Func<Task> verify = () => MonitoredContainerContract.VerifyAsync(container.Object, Path("/tenantId"), CancellationToken.None);

            (await verify.Should().ThrowExactlyAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("/tenantId").And.Contain("/ttl");
        }

        // DRIFT PIN: the contract rejects the paths the relay's own patch ops target, and the F2 (c) anchor forces the
        // configured status patch path to that same field. If either side moved, a container partitioned on the status
        // field would silently become acceptable again while the stamp still failed on it.
        [Fact]
        public void MustRejectTheVerySameStatusPathTheDeliverySettingsAnchorTheStatusPatchTo()
        {
            CosmosOutboxDocument.RelayStampedPaths.Should().Contain(OutboxDeliverySettings.Legacy.StatusPatchPath);
        }
    }
}
