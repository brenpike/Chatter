using System;
using System.IO;
using System.Text;
using System.Xml;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    // Projects a Service Broker system Error message body into the two sanitized values an operator log needs.
    // The <Description> of END CONVERSATION ... WITH ERROR is nvarchar(3000) copied verbatim from the dialog
    // PEER, so every byte reaching a log is peer-controlled; rationale in
    // docs/adr/0037-a-terminal-receive-outcome-ends-its-conversation-and-a-deterministic-sql-fault-is-not-retried.md.
    internal static class ServiceBrokerErrorPayload
    {
        internal const string NoErrorPayloadSentinel = "<no error payload>";
        internal const string UnreadableErrorPayloadSentinel = "<unreadable error payload>";
        internal const string TruncationMarker = "...[truncated]";

        // A DESCRIPTION is nvarchar(3000). Even with every one of those characters escaped to its longest
        // XML entity (&quot;), the documented Error document stays under 40 KB of UTF-16, so 64 KiB admits
        // every real payload while bounding what is decoded and parsed.
        internal const int MaxBodyLengthInBytes = 64 * 1024;
        internal const int MaxDescriptionLength = 3000;
        internal const int MaxCodeLength = 32;

        private const string ErrorNamespace = "http://schemas.microsoft.com/SQL/ServiceBroker/Error";
        private const string ErrorElementName = "Error";
        private const string CodeElementName = "Code";
        private const string DescriptionElementName = "Description";
        private const char ByteOrderMark = (char)0xFEFF;

        // INVARIANT: a body larger than MaxBodyLengthInBytes never reaches a projected value — it returns the
        // unreadable sentinel. Pinned by WhenDescribingAnErrorPayload
        // .MustReturnTheUnreadableSentinelWhenTheBodyExceedsTheParseBound. The same threshold is enforced
        // twice — this guard, which refuses the body BEFORE it is decoded, and TryReadErrorDocument's
        // MaxCharactersInDocument, which is exactly half of it because UTF-16 is two bytes per character — so
        // each alone is sufficient and removing EITHER one reddens nothing; removing BOTH reddens that oracle
        // (verified) (ADR-0027).
        //
        // Service Broker Error bodies are the documented <Error><Code/><Description/></Error> XML, encoded
        // UTF-16 (Encoding.Unicode) and prefixed with a UTF-16LE byte order mark (the bytes FF FE 3C 00,
        // observed live). Encoding.Unicode.GetString keeps that mark as a leading U+FEFF, which XmlReader
        // reading an already-decoded string rejects, so it is stripped explicitly; deleting the TrimStart reddens
        // MustParseABomPrefixedBody (verified). No test pins the encoding choice itself, but
        // ReceiveMessageFromQueueCommand's gzip test (SUBSTRING(message_body, 1, 2) = 0x1F8B) is what makes
        // UTF-16 safe to decode here: a UTF-16LE byte order mark is the byte pair 0xFF 0xFE, not the 0x1F 0x8B
        // gzip magic, so an Error body always passes the receive query through undecompressed.
        internal static ErrorDescription Describe(byte[] body)
        {
            if (body is null || body.Length == 0)
            {
                return ErrorDescription.Sanitized(NoErrorPayloadSentinel, NoErrorPayloadSentinel);
            }

            if (body.Length > MaxBodyLengthInBytes)
            {
                return Unreadable();
            }

            var document = Encoding.Unicode.GetString(body).TrimStart(ByteOrderMark);

            return TryReadErrorDocument(document, out var code, out var description)
                ? ErrorDescription.Sanitized(code, description)
                : Unreadable();
        }

        private static ErrorDescription Unreadable()
            => ErrorDescription.Sanitized(UnreadableErrorPayloadSentinel, UnreadableErrorPayloadSentinel);

        // INVARIANT: no document type definition is ever processed and no external resource is ever resolved —
        // DtdProcessing.Prohibit makes a DTD an XmlException and XmlResolver = null denies resolution. Pinned by
        // MustReturnTheUnreadableSentinelForADocumentThatDeclaresADtd, which switching DtdProcessing to Parse
        // reddens (verified) (ADR-0027).
        //
        // INVARIANT: only the documented Error document projects a value — a positive allowlist on the root
        // element's local name AND namespace, then on the Code and Description children, so anything else is
        // unrepresentable rather than echoed. Pinned by MustReturnTheUnreadableSentinelForANonErrorDocument,
        // which dropping the NamespaceURI half of the allowlist reddens (verified) (ADR-0027).
        private static bool TryReadErrorDocument(string document, out string code, out string description)
        {
            code = null;
            description = null;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBodyLengthInBytes / sizeof(char),
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                CloseInput = true
            };

            try
            {
                using var documentReader = new StringReader(document);
                using var reader = XmlReader.Create(documentReader, settings);

                if (reader.MoveToContent() != XmlNodeType.Element
                    || !IsErrorNamespaceElement(reader, ErrorElementName)
                    || reader.IsEmptyElement)
                {
                    return false;
                }

                ReadErrorChildren(reader, ref code, ref description);
            }
            catch (XmlException)
            {
                return false;
            }

            return code != null && description != null;
        }

        private static void ReadErrorChildren(XmlReader reader, ref string code, ref string description)
        {
            reader.Read();
            while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                // ReadElementContentAsString already leaves the reader on the node AFTER the element it read,
                // so each arm continues without advancing again.
                if (IsErrorNamespaceElement(reader, CodeElementName))
                {
                    code = reader.ReadElementContentAsString();
                    continue;
                }

                if (IsErrorNamespaceElement(reader, DescriptionElementName))
                {
                    description = reader.ReadElementContentAsString();
                    continue;
                }

                reader.Skip();
            }
        }

        private static bool IsErrorNamespaceElement(XmlReader reader, string localName)
            => reader.LocalName == localName && reader.NamespaceURI == ErrorNamespace;

        // INVARIANT: every projected value is bounded and carries no character that could forge a log record —
        // C0 controls, DEL, U+2028 and U+2029 each collapse to a single space. Pinned by
        // MustNeutraliseControlCharactersInTheDescription and
        // MustTruncateADescriptionLongerThanTheDescriptionBound, which returning value unchanged reddens both
        // (verified) (ADR-0027).
        private static string Sanitize(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var keptLength = Math.Min(value.Length, maxLength);
            var sanitized = new StringBuilder(keptLength + TruncationMarker.Length);
            for (var index = 0; index < keptLength; index++)
            {
                var character = value[index];
                sanitized.Append(IsLogUnsafe(character) ? ' ' : character);
            }

            if (value.Length > maxLength)
            {
                sanitized.Append(TruncationMarker);
            }

            return sanitized.ToString();
        }

        private static bool IsLogUnsafe(char character)
            => character < 0x20 || character == 0x7F || character == 0x2028 || character == 0x2029;

        /// <summary>
        /// The projected Service Broker error, carrying the sanitized code and description as separate values
        /// so each reaches a log as its own structured parameter.
        /// INVARIANT: <see cref="Sanitized"/> is the only producer and the constructor is private, so an
        /// unsanitized or unbounded field is unrepresentable. No test pins this — it is a construction
        /// property of the type, not a behaviour a test can drive (ADR-0027).
        /// </summary>
        internal readonly struct ErrorDescription
        {
            private ErrorDescription(string code, string description)
            {
                Code = code;
                Description = description;
            }

            internal string Code { get; }

            internal string Description { get; }

            internal static ErrorDescription Sanitized(string code, string description)
                => new ErrorDescription(Sanitize(code, MaxCodeLength), Sanitize(description, MaxDescriptionLength));
        }
    }
}
