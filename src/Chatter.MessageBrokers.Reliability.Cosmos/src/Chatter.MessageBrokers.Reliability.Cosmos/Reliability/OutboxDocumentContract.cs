using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    // The per-document sibling of MonitoredContainerContract, and the SOLE path from an admitted change-feed document to
    // a publishable message. Where that contract reconciles one CONTAINER's declared configuration against its ground
    // truth once at host start, this one reconciles one DOCUMENT's persisted fields against what the reconstruction
    // actually reads — in ONE evaluation, naming EVERY violation in ONE failure, off the very field-name constants the
    // reconstruction reads, so the check cannot drift from what it guards.
    //
    // It exists because a document that fails deterministically wedges its lease forever (#361): the drain throws, the
    // change-feed batch is never checkpointed, the same document re-surfaces, and the relay re-fails on it for as long
    // as it runs. Verifying the document up front turns that permanent wedge into a named, stampable verdict.
    //
    // TOTAL: it never throws, for any input. PURE: no I/O, no Container, no application-supplied delegate, no host
    // configuration — the document's own persisted bytes are its only input.
    //
    // THE POSITIVE RULE: a violation may be derived ONLY from deterministic evaluation over the document's own bytes.
    // Anything arising from host configuration or application-supplied code is NEVER a violation here, because a
    // redeploy or a registration fix makes it succeed on the very next pass — so treating it as one would give up on a
    // perfectly good document. Deliberately EXCLUDED: IBodyConverterFactory.CreateBodyConverter, the broker dispatcher,
    // IOutboxBodyResolver, and IMessagingInfrastructureProvider.GetDispatcher (it resolves purely from what the HOST
    // registered: a blank or absent type silently falls back to the host's default infrastructure, and only a named
    // type the host never registered throws KeyNotFoundException — either way that is a HOST defect, not a document
    // defect).
    //
    // INVARIANT: N=1 is correct BY CONSTRUCTION, not by threshold. The input is an immutable JsonElement and this
    // function is total and pure, so a second evaluation is provably identical to the first. There is deliberately no
    // attempt counter and no retry threshold here — none is representable, and none could learn anything. Equally, a
    // violation is never inferred from the ABSENCE of a disqualifier: nothing here catches an exception and concludes
    // the failure must be permanent.
    internal static class OutboxDocumentContract
    {
        // Verifies one change-feed document against the fields the reconstruction and the publish read, returning EITHER
        // a verified descriptor carrying EVERY document-derived value that path consumes — message id, resolved content
        // type, destination, body string, materialized message context and messaging system — OR every violation the
        // document's own bytes prove. The descriptor is the path's whole input, so no read of the document survives past
        // this verification.
        internal static OutboxDocumentVerification Verify(JsonElement document)
        {
            TryReadString(document, CosmosOutboxDocument.MessageIdField, out string messageId);
            TryReadString(document, CosmosOutboxDocument.DestinationField, out string destination);
            bool carriesMessageBody = TryReadString(document, CosmosOutboxDocument.MessageBodyField, out string messageBody);
            TryReadString(document, CosmosOutboxDocument.MessageContentTypeField, out string documentContentType);
            TryReadString(document, CosmosOutboxDocument.MessageContextField, out string serializedMessageContext);

            // EVERY check always runs and a single failure names every violation, for the same reason the
            // monitored-container contract does it: an operator who fixed one violation, redrained, and only then
            // discovered the next would pay a wedged lease per violation on one malformed document.
            var violations = new List<string>();
            AddWhenPresent(violations, DescribeDestinationViolation(destination));
            AddWhenPresent(violations, DescribeMessageBodyViolation(carriesMessageBody));
            AddWhenPresent(violations, DescribeMessageContextViolation(serializedMessageContext, out IDictionary<string, object> messageContext));

            string contentType = ResolveContentType(documentContentType, messageContext);
            AddWhenPresent(violations, DescribeContentTypeViolation(contentType));
            AddWhenPresent(violations, DescribeMessagingSystemViolation(messageContext));

            if (violations.Count > 0)
            {
                return OutboxDocumentVerification.Violated(BuildViolationMessage(messageId, violations), violations);
            }

            return OutboxDocumentVerification.Satisfied(messageId, contentType, destination, messageBody, messageContext, ReadMessagingSystem(messageContext));
        }

        private static void AddWhenPresent(ICollection<string> violations, string violation)
        {
            if (violation is not null)
            {
                violations.Add(violation);
            }
        }

        // OutboundBrokeredMessage's constructor throws ArgumentException for a blank destination, so a document that
        // carries none can never be reconstructed — no matter how often it is redrained.
        private static string DescribeDestinationViolation(string destination)
        {
            if (!string.IsNullOrWhiteSpace(destination))
            {
                return null;
            }

            return $"its '{CosmosOutboxDocument.DestinationField}' is absent, not a JSON string, or blank, and a brokered message requires a destination";
        }

        // The reconstruction hands the persisted body string straight to the body converter, which has no body bytes to
        // produce from a null. An EMPTY body string is NOT a violation: it converts to an empty byte array and publishes.
        private static string DescribeMessageBodyViolation(bool carriesMessageBody)
        {
            if (carriesMessageBody)
            {
                return null;
            }

            return $"its '{CosmosOutboxDocument.MessageBodyField}' is absent or not a JSON string, so there is no persisted body to publish";
        }

        // Materializes the persisted message context exactly as the reconstruction does. An absent, empty or whitespace
        // context is NOT a violation — it materializes to an empty context, which is what the drain already reads. Two
        // outcomes ARE violations: JSON the materializer rejects, and JSON that materializes to no context at all (a
        // literal `null`), which every downstream read of the context would fault on.
        private static string DescribeMessageContextViolation(string serializedMessageContext, out IDictionary<string, object> messageContext)
        {
            try
            {
                messageContext = MessageContext.MaterializePersistedContext(serializedMessageContext);
            }
            catch (Exception materializationFailure)
            {
                messageContext = null;
                return $"its '{CosmosOutboxDocument.MessageContextField}' is not JSON that materializes into a message context ({materializationFailure.Message})";
            }

            if (messageContext is null)
            {
                return $"its '{CosmosOutboxDocument.MessageContextField}' materializes to no message context at all";
            }

            return null;
        }

        // The reconstruction's content-type resolution: the document's own content type, falling back to the persisted
        // message context. The context value is read as a string rather than cast to one, so a context carrying a
        // non-string content type resolves nothing instead of faulting the drain. A context that did not materialize
        // supplies no fallback, so a document with no content type of its own is unresolvable either way.
        private static string ResolveContentType(string documentContentType, IDictionary<string, object> messageContext)
        {
            if (!string.IsNullOrWhiteSpace(documentContentType))
            {
                return documentContentType;
            }

            if (messageContext is null || !messageContext.TryGetValue(MessageContext.ContentType, out object contextContentType))
            {
                return null;
            }

            string contentType = contextContentType as string;
            return string.IsNullOrWhiteSpace(contentType) ? null : contentType;
        }

        private static string DescribeContentTypeViolation(string contentType)
        {
            if (contentType is not null)
            {
                return null;
            }

            return $"a content type is resolvable from neither its '{CosmosOutboxDocument.MessageContentTypeField}' nor its message context, so its body can never be serialized for publish";
        }

        // THE SINGLE READER of the Messaging Infrastructure identity a message context names: the one place in this
        // module that turns a persisted context value into the string the publish resolves a dispatcher with. The value
        // is read AS a string and NEVER cast to one, mirroring ResolveContentType above, so a non-string persisted kind
        // resolves nothing instead of faulting the drain with an InvalidCastException.
        // ONE READER, TWO POLICIES: on the DOCUMENT path Verify ALSO classifies a non-string kind as a violation, below;
        // on the resolver path the resolved message's context is HOST-owned and is never verified, so it is read here
        // WITHOUT classification.
        internal static string ReadMessagingSystem(IDictionary<string, object> messageContext)
        {
            if (!TryReadPersistedMessagingSystem(messageContext, out object persistedMessagingSystem))
            {
                return null;
            }

            string messagingSystem = persistedMessagingSystem as string;
            return string.IsNullOrWhiteSpace(messagingSystem) ? null : messagingSystem;
        }

        // The publish reads the messaging system AS a string, so a NON-STRING persisted KIND — a number, a bool, an
        // object, an array, or a strict ISO-8601 string, which the materializer turns into a DateTime — names no
        // messaging infrastructure and never will: the document's own bytes prove it.
        // ABSENT, JSON-null and BLANK are deliberately NOT violations. Each resolves to null, which the provider answers
        // from its OWN registrations — a HOST concern the positive rule excludes, because a redeploy fixes it and
        // giving up on the document would discard a perfectly good message.
        private static string DescribeMessagingSystemViolation(IDictionary<string, object> messageContext)
        {
            if (!TryReadPersistedMessagingSystem(messageContext, out object persistedMessagingSystem))
            {
                return null;
            }

            if (persistedMessagingSystem is null or string)
            {
                return null;
            }

            return $"its message context's '{MessageContext.InfrastructureType}' is persisted as a non-string value, so the messaging infrastructure it names can never be read";
        }

        // The one read of the messaging-system key, shared by the reader and the classification so the two can never
        // disagree about what the context carries. A context that did not materialize carries nothing at all.
        private static bool TryReadPersistedMessagingSystem(IDictionary<string, object> messageContext, out object persistedMessagingSystem)
        {
            if (messageContext is null)
            {
                persistedMessagingSystem = null;
                return false;
            }

            return messageContext.TryGetValue(MessageContext.InfrastructureType, out persistedMessagingSystem);
        }

        // Reads a string-valued property through the shared wire-shape reader, guarding the object-ness JsonElement's own
        // property reads demand: TryGetProperty THROWS on a non-object element, and totality admits every input,
        // including the undefined element. A document that is not an object simply carries none of the fields.
        private static bool TryReadString(JsonElement document, string propertyName, out string value)
        {
            if (document.ValueKind != JsonValueKind.Object)
            {
                value = null;
                return false;
            }

            return CosmosOutboxDocument.TryGetString(document, propertyName, out value);
        }

        private static string BuildViolationMessage(string messageId, IReadOnlyList<string> violations)
            => $"The Cosmos outbox relay cannot reconstruct outbox document '{messageId}' into a brokered message: {string.Join("; and ", violations)}. The document's own persisted fields prove this, so redraining it would fail identically; correct the writer that produced it.";
    }

    // The verdict of one Outbox Document Contract verification: EITHER the verified descriptor the reconstruction and the
    // publish need, OR the named violations. A satisfied verification carries no violations and a violated one carries
    // no descriptor, so a caller cannot publish a document the contract rejected.
    //
    // INVARIANT: this descriptor is the CLOSED set of document-derived values the verbatim path consumes. The path takes
    // this and no JsonElement, so a value reaching reconstruction or publish without having passed the contract is
    // UNREPRESENTABLE — there is no document in scope to read a further field from.
    internal sealed class OutboxDocumentVerification
    {
        private static readonly IReadOnlyList<string> _noViolations = Array.Empty<string>();

        private OutboxDocumentVerification(bool isSatisfied,
                                           string violationMessage,
                                           IReadOnlyList<string> violations,
                                           string messageId,
                                           string contentType,
                                           string destination,
                                           string messageBody,
                                           IDictionary<string, object> messageContext,
                                           string messagingSystem)
        {
            IsSatisfied = isSatisfied;
            ViolationMessage = violationMessage;
            Violations = violations;
            MessageId = messageId;
            ContentType = contentType;
            Destination = destination;
            MessageBody = messageBody;
            MessageContext = messageContext;
            MessagingSystem = messagingSystem;
        }

        internal static OutboxDocumentVerification Satisfied(string messageId,
                                                            string contentType,
                                                            string destination,
                                                            string messageBody,
                                                            IDictionary<string, object> messageContext,
                                                            string messagingSystem)
            => new OutboxDocumentVerification(true, null, _noViolations, messageId, contentType, destination, messageBody, messageContext, messagingSystem);

        internal static OutboxDocumentVerification Violated(string violationMessage, IReadOnlyList<string> violations)
            => new OutboxDocumentVerification(false, violationMessage, violations, null, null, null, null, null, null);

        /// <summary>Whether the document carries everything the reconstruction reads, in the shape it reads it.</summary>
        internal bool IsSatisfied { get; }

        /// <summary>Every violation the document's own bytes prove; empty when the verification is satisfied.</summary>
        internal IReadOnlyList<string> Violations { get; }

        /// <summary>The single failure naming every violation; null when the verification is satisfied.</summary>
        internal string ViolationMessage { get; }

        /// <summary>The verbatim persisted message id the brokered message is reconstructed with; null when violated.</summary>
        internal string MessageId { get; }

        /// <summary>The content type resolved from the document or its message context; null when violated.</summary>
        internal string ContentType { get; }

        /// <summary>The destination the brokered message publishes to; null when violated.</summary>
        internal string Destination { get; }

        /// <summary>The persisted body string the body converter turns into bytes; null when violated.</summary>
        internal string MessageBody { get; }

        /// <summary>The materialized message context; null when violated.</summary>
        internal IDictionary<string, object> MessageContext { get; }

        /// <summary>
        /// The Messaging Infrastructure the message context names, read as a string; null when the context names none,
        /// names a blank one, or when violated.
        /// </summary>
        internal string MessagingSystem { get; }
    }
}
