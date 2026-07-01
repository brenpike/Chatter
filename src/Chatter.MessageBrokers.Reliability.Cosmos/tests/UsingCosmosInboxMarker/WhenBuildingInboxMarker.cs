using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosInboxMarker
{
    public class WhenBuildingInboxMarker : Testing.Core.Context
    {
        private static IReadOnlyList<JsonElement> PartitionKeyValues(params string[] values)
        {
            var list = new List<JsonElement>(values.Length);
            foreach (var value in values)
            {
                list.Add(JsonDocument.Parse($"\"{value}\"").RootElement.Clone());
            }
            return list;
        }

        private static JsonElement Render(CosmosInboxMarker marker, IReadOnlyList<string> partitionKeyPath, IReadOnlyList<JsonElement> partitionKeyValues)
        {
            var document = marker.ToJsonObject(partitionKeyPath, partitionKeyValues);
            var json = document.ToJsonString(ChatterJson.Options);
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        [Fact]
        public void MustCarryInboxDiscriminator()
        {
            var marker = CosmosInboxMarker.From("msg-1");

            var document = Render(marker, new[] { "/tenantId" }, PartitionKeyValues("tenant-1"));

            document.GetProperty("_chatterType").GetString().Should().Be("inbox");
        }

        [Fact]
        public void MustEncodeIdViaForInboxAndStoreRawMessageIdVerbatim()
        {
            var rawMessageId = "msg/with?reserved#chars";
            var marker = CosmosInboxMarker.From(rawMessageId);

            var document = Render(marker, new[] { "/tenantId" }, PartitionKeyValues("tenant-1"));

            document.GetProperty("id").GetString().Should().Be(CosmosItemId.ForInbox(rawMessageId));
            document.GetProperty("id").GetString().Should().StartWith("inbox:");
            // The encoded id segment must not carry Cosmos-forbidden characters lifted from the raw id.
            document.GetProperty("id").GetString().Should().NotContainAny("/", "\\", "?", "#");
            document.GetProperty("MessageId").GetString().Should().Be(rawMessageId);
        }

        [Fact]
        public void MustCarryReceivedAtUtcAndNoStatusNoTtl()
        {
            var before = DateTime.UtcNow;
            var marker = CosmosInboxMarker.From("msg-1");
            var after = DateTime.UtcNow;

            var document = Render(marker, new[] { "/tenantId" }, PartitionKeyValues("tenant-1"));

            // ReceivedAtUtc is present and serialized as an ISO-8601 instant within the build window.
            var receivedAt = document.GetProperty("ReceivedAtUtc").GetDateTime();
            receivedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);

            // NO status field (inbox markers are not relayed) and NO TTL field (markers persist for the dedup window).
            document.TryGetProperty("status", out _).Should().BeFalse("inbox markers carry no delivery status");
            document.TryGetProperty("ttl", out _).Should().BeFalse("inbox markers carry no TTL");
            document.TryGetProperty("_ts", out _).Should().BeFalse("the Chatter wire shape stamps no Cosmos system fields");
            // NO completion field when the caller does not opt in (byte-identical to the pre-amendment / document-tier shape).
            document.TryGetProperty("Completed", out _).Should().BeFalse("a marker with no completion opt-in carries no Completed field");
        }

        [Fact]
        public void MustCarryCompletedFieldOnlyWhenOptedIn()
        {
            // ADR-0009 D1 amendment: the standalone inbox opts into the two-phase completion state — a pending claim
            // renders Completed=false, a completed marker renders Completed=true.
            var pendingMarker = CosmosInboxMarker.From("msg-1", completed: false);
            var completedMarker = CosmosInboxMarker.From("msg-1", completed: true);

            var pendingDocument = Render(pendingMarker, new[] { "/tenantId" }, PartitionKeyValues("tenant-1"));
            var completedDocument = Render(completedMarker, new[] { "/tenantId" }, PartitionKeyValues("tenant-1"));

            pendingDocument.GetProperty("Completed").GetBoolean().Should().BeFalse("a pending standalone claim carries Completed=false");
            completedDocument.GetProperty("Completed").GetBoolean().Should().BeTrue("a completed standalone marker carries Completed=true");
        }

        [Fact]
        public void MustStampPartitionKeyValueAtContainerPathNotFixedField()
        {
            var marker = CosmosInboxMarker.From("msg-1");

            var document = Render(marker, new[] { "/tenantId" }, PartitionKeyValues("tenant-1"));

            document.GetProperty("tenantId").GetString().Should().Be("tenant-1");
            document.TryGetProperty("partitionKey", out _).Should().BeFalse("the value rides the container's actual PK path, not a fixed field");
        }

        [Fact]
        public void MustStampHierarchicalPartitionKeyValuesAtNestedPaths()
        {
            var marker = CosmosInboxMarker.From("msg-1");

            var document = Render(marker, new[] { "/tenant/id", "/region" }, PartitionKeyValues("acme", "us-east"));

            document.GetProperty("tenant").GetProperty("id").GetString().Should().Be("acme");
            document.GetProperty("region").GetString().Should().Be("us-east");
        }

        [Fact]
        public void MustMintFreshNodesAcrossMultipleMarkersWithoutReparent()
        {
            // Regression guard: building two markers from the same partition-key value set must not reparent a shared
            // JsonNode (which would throw on the second stamp). Each ToJsonObject call mints fresh nodes.
            var partitionKeyPath = new[] { "/tenant/id", "/region" };
            var partitionKeyValues = PartitionKeyValues("acme", "us-east");

            var marker0 = CosmosInboxMarker.From("msg-0");
            var marker1 = CosmosInboxMarker.From("msg-1");

            var doc0 = Render(marker0, partitionKeyPath, partitionKeyValues);
            var doc1 = Render(marker1, partitionKeyPath, partitionKeyValues);

            doc0.GetProperty("tenant").GetProperty("id").GetString().Should().Be("acme");
            doc1.GetProperty("tenant").GetProperty("id").GetString().Should().Be("acme");
            doc0.GetProperty("region").GetString().Should().Be("us-east");
            doc1.GetProperty("region").GetString().Should().Be("us-east");
            doc0.GetProperty("MessageId").GetString().Should().Be("msg-0");
            doc1.GetProperty("MessageId").GetString().Should().Be("msg-1");
        }

        [Theory]
        [InlineData("/id")]
        [InlineData("/_chatterType")]
        [InlineData("/MessageId")]
        [InlineData("/ReceivedAtUtc")]
        public void MustFailLoudlyWhenPartitionKeyPathCollidesWithReservedRootField(string reservedPath)
        {
            var marker = CosmosInboxMarker.From("msg-1");

            Action act = () => marker.ToJsonObject(new[] { reservedPath }, PartitionKeyValues("tenant-1"));

            // A reserved-root collision must throw rather than silently overwrite a required marker field (e.g. /id
            // would replace the deterministic inbox id, defeating the dedup).
            act.Should().Throw<InvalidOperationException>();
        }
    }
}
