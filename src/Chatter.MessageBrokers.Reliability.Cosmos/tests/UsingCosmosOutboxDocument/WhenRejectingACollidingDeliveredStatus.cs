using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxDocument
{
    // Covers F2 (d): the configurable delivered status value may not collide with the fixed terminal status of an
    // Undeliverable Outbox Document. A collision would make a delivered document indistinguishable from one the relay
    // gave up on, so the operator's evidence of the defect would be a lie — the collision is rejected at construction
    // rather than validated later.
    public class WhenRejectingACollidingDeliveredStatus : Testing.Core.Context
    {
        // Constructs delivery settings with only the delivered status value diverged from a known-safe baseline, so a
        // construction-throw test isolates exactly the rejected status collision.
        private static Action ConstructSettings(string deliveredStatusValue)
            => () => new OutboxDeliverySettings(
                deliveredTtlSeconds: 86400,
                statusPatchPath: "/" + CosmosOutboxDocument.StatusField,
                deliveredStatusValue: deliveredStatusValue,
                additionalPendingFilter: null);

        [Fact]
        public void MustRejectDeliveredStatusEqualToUndeliverable()
        {
            ConstructSettings(CosmosOutboxDocument.StatusUndeliverable).Should().Throw<ArgumentException>(
                "a delivered status equal to the terminal undeliverable status would stamp a successfully published document as the evidence of a defect, so it must be unconstructable");
        }

        [Fact]
        public void MustAcceptADeliveredStatusThatCollidesWithNeitherPendingNorUndeliverable()
        {
            ConstructSettings(CosmosOutboxDocument.StatusDelivered).Should().NotThrow(
                "the baseline delivered status collides with no reserved status, so the collision guard must not reject it");
        }
    }
}
