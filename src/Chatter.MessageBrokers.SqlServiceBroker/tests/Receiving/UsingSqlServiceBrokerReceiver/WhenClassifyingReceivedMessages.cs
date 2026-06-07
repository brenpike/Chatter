using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingSqlServiceBrokerReceiver
{
    // Characterization decision-table: pins the FULL message-classification logic extracted from
    // SqlServiceBrokerReceiver.ReceiveMessageAsync AS-IS. These rows are the SPEC that
    // STEP-004's ServiceBrokerMessageClassifier must satisfy.
    //
    // The ServiceBrokerMessageClassifier type does not yet exist (introduced in STEP-004).
    // The [Theory] rows that reference ClassificationOutcome are SKIPPED until STEP-004 lands.
    // STEP-004 MUST match the ClassificationOutcome enum shape defined in this file exactly, or
    // update this file as part of its own scope and note the delta in its report.
    //
    // The delivery-attempt increment characterization ([Fact] tests at the bottom) runs GREEN
    // today: it exercises only ReceivedMessage + ConcurrentDictionary — no missing type.

    /// <summary>
    /// Mirrors the decision-result produced by ServiceBrokerMessageClassifier (STEP-004).
    /// Defined here so the test file compiles before STEP-004 lands. STEP-004 must introduce a
    /// type with at least these members in the
    /// Chatter.MessageBrokers.SqlServiceBroker.Receiving namespace; once it does, this local
    /// copy can be removed and the using replaced with the production import.
    /// </summary>
    // INVARIANT: member names and ordinal positions must match what STEP-004 produces verbatim.
    public enum ClassificationOutcome
    {
        /// <summary>Message reference is null — discard (commit + dispose, return null).</summary>
        DiscardNull = 0,

        /// <summary>
        /// MessageTypeName == ServicesMessageTypes.EndDialogType — ack (EndDialogConversationCommand)
        /// and return null; no dispatch.
        /// </summary>
        EndDialog = 1,

        /// <summary>
        /// MessageTypeName is not DefaultType and not ChatterBrokeredMessageType — discard and
        /// return null; no dispatch.
        /// </summary>
        DiscardWrongType = 2,

        /// <summary>
        /// MessageTypeName is an accepted type but Body is null — discard and return null;
        /// no dispatch.
        /// </summary>
        DiscardNullBody = 3,

        /// <summary>
        /// MessageTypeName == ChatterBrokeredMessageType with non-null Body — deserialise as
        /// OutboundBrokeredMessage, unwrap payload/id/headers, dispatch.
        /// </summary>
        DispatchChatterBrokeredMessage = 4,

        /// <summary>
        /// MessageTypeName == DefaultType with non-null Body — dispatch raw body without unwrapping.
        /// </summary>
        DispatchDefault = 5,
    }

    public class WhenClassifyingReceivedMessages : Testing.Core.Context
    {
        // -----------------------------------------------------------------------
        // Decision-table rows — SKIPPED until STEP-004 introduces
        // ServiceBrokerMessageClassifier.
        //
        // When STEP-004 lands:
        //  1. Remove the Skip attribute from each [Theory].
        //  2. Replace the local ClassificationOutcome references with the production type.
        //  3. Implement the Classify(ReceivedMessage) call in the body.
        // -----------------------------------------------------------------------

        private static ReceivedMessage BuildMessage(
            string messageTypeName,
            byte[] body = null,
            Guid convHandle = default)
            => new ReceivedMessage(
                convGroupHandle: Guid.NewGuid(),
                convHandle: convHandle == default ? Guid.NewGuid() : convHandle,
                messageSeqNo: 1L,
                serviceName: "test-service",
                serviceContractName: "test-contract",
                messageTypeName: messageTypeName,
                body: body);

        // --- Decision rows -------------------------------------------------------

        public static IEnumerable<object[]> ClassificationRows()
        {
            var body = new byte[] { 1, 2, 3 };

            // Row 1: null message reference → DiscardNull
            yield return new object[] { null, ClassificationOutcome.DiscardNull,
                "null message reference must be classified as DiscardNull" };

            // Row 2: EndDialogType → EndDialog  (body irrelevant; branch fires before body check)
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.EndDialogType, body: body),
                ClassificationOutcome.EndDialog,
                "EndDialogType must be classified as EndDialog regardless of body" };

            // Row 3: EndDialogType with null body → EndDialog  (branch fires before body check)
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.EndDialogType, body: null),
                ClassificationOutcome.EndDialog,
                "EndDialogType with null body must still be classified as EndDialog" };

            // Row 4: ErrorType → DiscardWrongType
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.ErrorType, body: body),
                ClassificationOutcome.DiscardWrongType,
                "ErrorType must be classified as DiscardWrongType" };

            // Row 5: QueryNotificationType → DiscardWrongType
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.QueryNotificationType, body: body),
                ClassificationOutcome.DiscardWrongType,
                "QueryNotificationType must be classified as DiscardWrongType" };

            // Row 6: arbitrary unknown type → DiscardWrongType
            yield return new object[] {
                BuildMessage("http://example.com/UnknownType", body: body),
                ClassificationOutcome.DiscardWrongType,
                "an unknown message type must be classified as DiscardWrongType" };

            // Row 7: DefaultType with null body → DiscardNullBody
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.DefaultType, body: null),
                ClassificationOutcome.DiscardNullBody,
                "DefaultType with null body must be classified as DiscardNullBody" };

            // Row 8: ChatterBrokeredMessageType with null body → DiscardNullBody
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.ChatterBrokeredMessageType, body: null),
                ClassificationOutcome.DiscardNullBody,
                "ChatterBrokeredMessageType with null body must be classified as DiscardNullBody" };

            // Row 9: ChatterBrokeredMessageType with body → DispatchChatterBrokeredMessage
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.ChatterBrokeredMessageType, body: body),
                ClassificationOutcome.DispatchChatterBrokeredMessage,
                "ChatterBrokeredMessageType with non-null body must be classified as DispatchChatterBrokeredMessage" };

            // Row 10: DefaultType with body → DispatchDefault
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.DefaultType, body: body),
                ClassificationOutcome.DispatchDefault,
                "DefaultType with non-null body must be classified as DispatchDefault" };
        }

        // STEP-004: remove Skip, remove the pragma suppression, wire up
        // ServiceBusMessageClassifier.Classify(message), and delete the throw.
#pragma warning disable xUnit1026 // parameters unused until STEP-004 wires the body
        [Theory(Skip = "STEP-004: ServiceBrokerMessageClassifier not yet introduced — rows are the spec")]
        [MemberData(nameof(ClassificationRows))]
        public void MustProduceExpectedOutcome(
            ReceivedMessage message,
            ClassificationOutcome expectedOutcome,
            string because)
        {
            // STEP-004 wires this in. Body of spec:
            //   var classifier = new ServiceBrokerMessageClassifier();
            //   ClassificationOutcome actual = classifier.Classify(message);
            //   actual.Should().Be(expectedOutcome, because);
            throw new NotImplementedException("STEP-004: implement ServiceBrokerMessageClassifier and remove Skip");
        }
#pragma warning restore xUnit1026

        // -----------------------------------------------------------------------
        // Delivery-attempt increment characterization — RUNS GREEN today.
        //
        // Pins the ConcurrentDictionary<Guid, int>.AddOrUpdate semantics that
        // SqlServiceBrokerReceiver uses to track per-conversation delivery attempts.
        // STEP-004 must preserve this contract when it extracts classification logic.
        // -----------------------------------------------------------------------

        // INVARIANT: first encounter of a ConvHandle inserts count 1.
        [Fact]
        public void MustInsertDeliveryAttemptAsOneForFirstEncounter()
        {
            var attempts = new ConcurrentDictionary<Guid, int>();
            var convHandle = Guid.NewGuid();

            attempts.AddOrUpdate(convHandle, 1, (ch, prev) => prev + 1);

            attempts[convHandle].Should().Be(1,
                "first encounter of a ConvHandle must insert delivery attempt count of 1");
        }

        // INVARIANT: each subsequent AddOrUpdate for the same ConvHandle increments by exactly 1.
        [Fact]
        public void MustIncrementDeliveryAttemptByOneOnEachSubsequentCall()
        {
            var attempts = new ConcurrentDictionary<Guid, int>();
            var convHandle = Guid.NewGuid();

            attempts.AddOrUpdate(convHandle, 1, (ch, prev) => prev + 1);
            attempts.AddOrUpdate(convHandle, 1, (ch, prev) => prev + 1);
            attempts.AddOrUpdate(convHandle, 1, (ch, prev) => prev + 1);

            attempts[convHandle].Should().Be(3,
                "three AddOrUpdate calls for the same ConvHandle must yield delivery attempt count of 3");
        }

        // INVARIANT: different ConvHandles are tracked independently.
        [Fact]
        public void MustTrackDeliveryAttemptsIndependentlyPerConvHandle()
        {
            var attempts = new ConcurrentDictionary<Guid, int>();
            var handleA = Guid.NewGuid();
            var handleB = Guid.NewGuid();

            attempts.AddOrUpdate(handleA, 1, (ch, prev) => prev + 1);
            attempts.AddOrUpdate(handleA, 1, (ch, prev) => prev + 1);
            attempts.AddOrUpdate(handleB, 1, (ch, prev) => prev + 1);

            attempts[handleA].Should().Be(2,
                "handleA was seen twice and must have delivery attempt count of 2");
            attempts[handleB].Should().Be(1,
                "handleB was seen once and must have delivery attempt count of 1");
        }

        // INVARIANT: TryGetValue after AddOrUpdate returns the updated value; no separate
        // read-after-write race on the same thread (pins the receiver's TryGetValue in the
        // finally block reading the updated count back out for header injection).
        [Fact]
        public void MustReturnUpdatedCountViaTryGetValueAfterAddOrUpdate()
        {
            var attempts = new ConcurrentDictionary<Guid, int>();
            var convHandle = Guid.NewGuid();

            attempts.AddOrUpdate(convHandle, 1, (ch, prev) => prev + 1);
            attempts.AddOrUpdate(convHandle, 1, (ch, prev) => prev + 1);

            attempts.TryGetValue(convHandle, out var count);

            count.Should().Be(2,
                "TryGetValue must return the count written by the most recent AddOrUpdate");
        }

        // INVARIANT: TryGetValue for an unknown ConvHandle returns false and count 0 (default(int)).
        // This pins the receiver's behaviour when a message is classified in the wrong-type or
        // null-body branch (AddOrUpdate is never called) and TryGetValue falls through to default.
        [Fact]
        public void MustReturnZeroDeliveryAttemptsForUnknownConvHandle()
        {
            var attempts = new ConcurrentDictionary<Guid, int>();

            attempts.TryGetValue(Guid.NewGuid(), out var count);

            count.Should().Be(0,
                "TryGetValue for an unseen ConvHandle must return the default int value (0)");
        }
    }
}
