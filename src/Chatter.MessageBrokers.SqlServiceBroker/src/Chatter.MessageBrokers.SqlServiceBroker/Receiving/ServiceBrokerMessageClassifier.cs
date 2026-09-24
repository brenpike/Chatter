namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    /// <summary>
    /// The decision produced by <see cref="ServiceBrokerMessageClassifier"/> for a received
    /// Service Broker message. Each outcome maps to one branch of
    /// <see cref="SqlServiceBrokerReceiver"/>'s receive flow; the ordinal positions encode the
    /// branch ORDER, which is significant
    /// (null → end-dialog → error → wrong-type → null-body → dispatch).
    /// </summary>
    internal enum ClassificationOutcome
    {
        /// <summary>Message reference is null — discard (commit + dispose, return null).</summary>
        DiscardNull = 0,

        /// <summary>
        /// MessageTypeName == ServicesMessageTypes.EndDialogType — ack (EndDialogConversationCommand)
        /// and return null; no dispatch. Fires BEFORE the body check.
        /// </summary>
        EndDialog = 1,

        /// <summary>
        /// MessageTypeName == ServicesMessageTypes.ErrorType — the conversation has faulted and can
        /// carry no further message; discard and end the conversation, return null; no dispatch.
        /// </summary>
        DiscardErroredConversation = 2,

        /// <summary>
        /// MessageTypeName is not DefaultType and not ChatterBrokeredMessageType — discard and
        /// return null; no dispatch.
        /// </summary>
        DiscardWrongType = 3,

        /// <summary>
        /// MessageTypeName is an accepted type but Body is null — discard and return null;
        /// no dispatch.
        /// </summary>
        DiscardNullBody = 4,

        /// <summary>
        /// MessageTypeName == ChatterBrokeredMessageType with non-null Body — deserialise as
        /// OutboundBrokeredMessage, unwrap payload/id/headers, dispatch.
        /// </summary>
        DispatchChatterBrokeredMessage = 5,

        /// <summary>
        /// MessageTypeName == DefaultType with non-null Body — dispatch raw body without unwrapping.
        /// </summary>
        DispatchDefault = 6,
    }

    /// <summary>
    /// Pure, I/O-free classification of a received Service Broker message into a
    /// <see cref="ClassificationOutcome"/>. This is the decision table for
    /// <see cref="SqlServiceBrokerReceiver"/>'s receive flow, preserving the exact ordering
    /// (null → end-dialog → error → wrong-type → null-body → dispatch). No SQL, no connection, no
    /// transaction — only the received message's type name and body are read.
    /// </summary>
    internal class ServiceBrokerMessageClassifier
    {
        public ClassificationOutcome Classify(ReceivedMessage message)
        {
            // INVARIANT: branch order is significant and must match SqlServiceBrokerReceiver.
            // - EndDialog fires before the null-body check, so an EndDialogType with a null body
            //   classifies EndDialog, NOT DiscardNullBody. Pinned by
            //   WhenClassifyingReceivedMessages.MustClassifyAnEndDialogBeforeTheTypeAndBodyChecks;
            //   moving the null-body check ahead of the end-dialog branch reddens it.
            // - EndDialog fires before the wrong-type filter, so an EndDialogType (which matches
            //   neither DefaultType nor ChatterBrokeredMessageType) classifies EndDialog, NOT
            //   DiscardWrongType. Pinned by the same
            //   MustClassifyAnEndDialogBeforeTheTypeAndBodyChecks; moving the wrong-type filter
            //   ahead of the end-dialog branch reddens it.
            // - The Error branch fires before the wrong-type filter, so an ErrorType (which also
            //   matches neither DefaultType nor ChatterBrokeredMessageType) classifies
            //   DiscardErroredConversation, NOT DiscardWrongType. Pinned by
            //   MustProduceExpectedOutcome row 4 (ErrorType); moving the wrong-type filter ahead
            //   of the Error branch reddens it.
            // - The Error branch fires before the null-body check, so an ErrorType with a null
            //   body classifies DiscardErroredConversation, NOT DiscardNullBody. Pinned by
            //   MustProduceExpectedOutcome row 5 (ErrorType, null body); moving the null-body
            //   check ahead of the Error branch reddens it.
            if (message is null)
            {
                return ClassificationOutcome.DiscardNull;
            }

            if (message.MessageTypeName == ServicesMessageTypes.EndDialogType)
            {
                return ClassificationOutcome.EndDialog;
            }

            if (message.MessageTypeName == ServicesMessageTypes.ErrorType)
            {
                return ClassificationOutcome.DiscardErroredConversation;
            }

            if (message.MessageTypeName != ServicesMessageTypes.DefaultType && message.MessageTypeName != ServicesMessageTypes.ChatterBrokeredMessageType)
            {
                return ClassificationOutcome.DiscardWrongType;
            }

            if (message.Body == null)
            {
                return ClassificationOutcome.DiscardNullBody;
            }

            if (message.MessageTypeName == ServicesMessageTypes.ChatterBrokeredMessageType)
            {
                return ClassificationOutcome.DispatchChatterBrokeredMessage;
            }

            return ClassificationOutcome.DispatchDefault;
        }

        /// <summary>
        /// Whether <paramref name="outcome"/> settles a real received message and so obliges its
        /// caller to end that message's conversation. The two dispatch outcomes are excluded because
        /// a dispatched message is settled later by its ack, nack or deadletter; DiscardNull is
        /// excluded because there is no message and no conversation handle to end. The END
        /// CONVERSATION itself, and its transaction-mode scope, live at
        /// SqlServiceBrokerReceiver.DiscardMessageAsync.
        /// </summary>
        // INVARIANT: this returns true for exactly EndDialog, DiscardErroredConversation,
        // DiscardWrongType and DiscardNullBody, and false for every other outcome. Pinned by
        // WhenClassifyingReceivedMessages.MustEndTheConversationForEveryOutcomeThatSettlesAReceivedMessage;
        // returning false for DiscardWrongType reddens it. Every declared outcome has a row in that
        // theory, pinned by MustCoverEveryClassificationOutcomeInTheEndsConversationRows; adding a
        // ClassificationOutcome member without adding its row reddens it. Rationale:
        // docs/adr/0037-a-terminal-receive-outcome-ends-its-conversation-and-a-deterministic-sql-fault-is-not-retried.md
        // (Decision 1).
        internal static bool EndsConversation(ClassificationOutcome outcome)
            => outcome == ClassificationOutcome.EndDialog
            || outcome == ClassificationOutcome.DiscardErroredConversation
            || outcome == ClassificationOutcome.DiscardWrongType
            || outcome == ClassificationOutcome.DiscardNullBody;
    }
}
