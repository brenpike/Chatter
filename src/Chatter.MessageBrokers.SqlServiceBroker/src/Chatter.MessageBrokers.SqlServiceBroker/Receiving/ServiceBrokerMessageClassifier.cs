namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    /// <summary>
    /// The decision produced by <see cref="ServiceBrokerMessageClassifier"/> for a received
    /// Service Broker message. Each outcome maps to one branch extracted VERBATIM from
    /// <see cref="SqlServiceBrokerReceiver"/>'s receive flow; the ordinal positions encode the
    /// branch ORDER, which is significant (null → end-dialog → wrong-type → null-body → dispatch).
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

    /// <summary>
    /// Pure, I/O-free classification of a received Service Broker message into a
    /// <see cref="ClassificationOutcome"/>. The branch logic is extracted VERBATIM from
    /// <see cref="SqlServiceBrokerReceiver"/>'s receive flow, preserving the exact ordering
    /// (null → end-dialog → wrong-type → null-body → dispatch). No SQL, no connection, no
    /// transaction — only the received message's type name and body are read.
    /// </summary>
    internal class ServiceBrokerMessageClassifier
    {
        public ClassificationOutcome Classify(ReceivedMessage message)
        {
            // INVARIANT: branch order is significant and must match SqlServiceBrokerReceiver
            // verbatim. EndDialog fires before the body check, so an EndDialogType with a null
            // body classifies EndDialog, NOT DiscardNullBody.
            if (message is null)
            {
                return ClassificationOutcome.DiscardNull;
            }

            if (message.MessageTypeName == ServicesMessageTypes.EndDialogType)
            {
                return ClassificationOutcome.EndDialog;
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
    }
}
