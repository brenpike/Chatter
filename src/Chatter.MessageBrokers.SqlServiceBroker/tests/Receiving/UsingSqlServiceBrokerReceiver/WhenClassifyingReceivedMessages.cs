using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingSqlServiceBrokerReceiver
{
    // Decision-table: pins the FULL message-classification logic of
    // SqlServiceBrokerReceiver.ReceiveMessageAsync. These rows are the SPEC that
    // ServiceBrokerMessageClassifier must satisfy. The rows were originally a verbatim
    // characterization of the receive flow and have since been amended — the Error type now
    // classifies as DiscardErroredConversation rather than DiscardWrongType (#357).
    //
    // The classification rows are exercised against the production
    // ServiceBrokerMessageClassifier (introduced in STEP-004); the ClassificationOutcome enum
    // is the production type from Chatter.MessageBrokers.SqlServiceBroker.Receiving.
    //
    // The delivery-attempt increment characterization ([Fact] tests at the bottom) runs GREEN
    // today: it exercises only ReceivedMessage + ConcurrentDictionary — no missing type.

    public class WhenClassifyingReceivedMessages : Testing.Core.Context
    {
        // -----------------------------------------------------------------------
        // Decision-table rows — bound against the production
        // ServiceBrokerMessageClassifier (STEP-004).
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

            // Row 4: ErrorType → DiscardErroredConversation
            yield return new object[] {
                BuildMessage(ServicesMessageTypes.ErrorType, body: body),
                ClassificationOutcome.DiscardErroredConversation,
                "the Error type is classified separately from any other unaccepted type so the receiver can end the errored conversation rather than leaving it open" };

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

        // INVARIANT: this method is internal (not public) because its ClassificationOutcome
        // parameter is an internal production type; a public method with a less-accessible
        // parameter trips CS0051. The test assembly sees the type via InternalsVisibleTo.
        [Theory]
        [MemberData(nameof(ClassificationRows))]
        internal void MustProduceExpectedOutcome(
            ReceivedMessage message,
            ClassificationOutcome expectedOutcome,
            string because)
        {
            var classifier = new ServiceBrokerMessageClassifier();

            ClassificationOutcome actual = classifier.Classify(message);

            actual.Should().Be(expectedOutcome, because);
        }

        [Fact]
        public void MustStillClassifyAnEndDialogBeforeTheErrorCheck()
        {
            var classifier = new ServiceBrokerMessageClassifier();

            var endDialog = classifier.Classify(BuildMessage(ServicesMessageTypes.EndDialogType, body: new byte[] { 1, 2, 3 }));
            var endDialogWithNullBody = classifier.Classify(BuildMessage(ServicesMessageTypes.EndDialogType, body: null));

            endDialog.Should().Be(ClassificationOutcome.EndDialog,
                "inserting the Error branch must not move the end-dialog branch out of its position ahead of it");
            endDialogWithNullBody.Should().Be(ClassificationOutcome.EndDialog,
                "the end-dialog branch still fires before the body check, so a null body must not change its outcome");
        }

        // -----------------------------------------------------------------------
        // Ends-conversation rule — every outcome that settles a real received message
        // ends that message's conversation. See
        // docs/adr/0037-a-terminal-receive-outcome-ends-its-conversation-and-a-deterministic-sql-fault-is-not-retried.md.
        // -----------------------------------------------------------------------

        public static IEnumerable<object[]> EndsConversationRows()
        {
            yield return new object[] { ClassificationOutcome.EndDialog, true,
                "an end-dialog message settles the received message and must end its conversation" };
            yield return new object[] { ClassificationOutcome.DiscardErroredConversation, true,
                "an errored conversation is terminal and must be ended rather than left open" };
            yield return new object[] { ClassificationOutcome.DiscardWrongType, true,
                "a wrong-type message will never be dispatched, so its conversation must be ended" };
            yield return new object[] { ClassificationOutcome.DiscardNullBody, true,
                "a null-body message will never be dispatched, so its conversation must be ended" };
            yield return new object[] { ClassificationOutcome.DiscardNull, false,
                "there is no received message and no conversation handle to end" };
            yield return new object[] { ClassificationOutcome.DispatchChatterBrokeredMessage, false,
                "a dispatched message is settled later by its ack, nack or deadletter" };
            yield return new object[] { ClassificationOutcome.DispatchDefault, false,
                "a dispatched message is settled later by its ack, nack or deadletter" };
        }

        // INVARIANT: this method is internal for the same CS0051 reason as
        // MustProduceExpectedOutcome — its ClassificationOutcome parameter is internal.
        [Theory]
        [MemberData(nameof(EndsConversationRows))]
        internal void MustEndTheConversationForEveryOutcomeThatSettlesAReceivedMessage(
            ClassificationOutcome outcome,
            bool expectedEndsConversation,
            string because)
            => ServiceBrokerMessageClassifier.EndsConversation(outcome)
                .Should().Be(expectedEndsConversation, because);

        // INVARIANT: the ends-conversation theory above enumerates every ClassificationOutcome,
        // so a newly added outcome cannot ship without a stated ends-conversation decision.
        [Fact]
        public void MustCoverEveryClassificationOutcomeInTheEndsConversationRows()
        {
            var coveredOutcomes = new HashSet<object>();
            foreach (var row in EndsConversationRows())
            {
                coveredOutcomes.Add(row[0]);
            }

            coveredOutcomes.Count.Should().Be(
                Enum.GetValues(typeof(ClassificationOutcome)).Length,
                "every ClassificationOutcome member must have an ends-conversation row");
        }

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
