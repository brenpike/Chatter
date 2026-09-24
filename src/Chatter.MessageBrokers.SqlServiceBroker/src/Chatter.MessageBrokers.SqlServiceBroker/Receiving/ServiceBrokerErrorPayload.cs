using System;
using System.Buffers;
using System.Globalization;
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

        // throwOnInvalidBytes makes the decoder REFUSE a body that is not well-formed UTF-16 instead of
        // substituting U+FFFD, so no repaired byte is ever projected. bigEndian: false matches the UTF-16LE
        // Service Broker writes; byteOrderMark governs GetPreamble only and strips nothing on decode.
        private static readonly UnicodeEncoding StrictUnicode =
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);

        // INVARIANT: a body larger than MaxBodyLengthInBytes never reaches a projected value — it returns the
        // unreadable sentinel. Pinned by WhenDescribingAnErrorPayload
        // .MustReturnTheUnreadableSentinelWhenTheBodyExceedsTheParseBound. The same threshold is enforced
        // twice — this guard, which refuses the body BEFORE it is decoded, and TryReadErrorDocument's
        // MaxCharactersInDocument, which is exactly half of it because UTF-16 is two bytes per character — so
        // each alone is sufficient and removing EITHER one reddens nothing; removing BOTH reddens that oracle
        // (verified) (ADR-0027).
        //
        // INVARIANT: a body that is not well-formed UTF-16 never reaches a projected value — StrictUnicode
        // throws rather than repairing the bytes. Pinned by
        // MustReturnTheUnreadableSentinelForABodyWithInvalidUtf16, which reverting the decode to
        // Encoding.Unicode.GetString reddens (verified). An odd byte count is refused by this same decode, but
        // MustReturnTheUnreadableSentinelForAnOddLengthBody does NOT pin that: the same mutation leaves it
        // green, because a lenient decode yields a U+FFFD the allowlist parse refuses anyway (verified)
        // (ADR-0027).
        //
        // NOTE: Service Broker Error bodies are the documented <Error><Code/><Description/></Error> XML, encoded
        // UTF-16LE and prefixed with a byte order mark (the bytes FF FE 3C 00, observed live). Decoding keeps
        // that mark as a leading U+FEFF — the encoding's byteOrderMark constructor argument governs
        // GetPreamble only, never the decode — and XmlReader reading an already-decoded string rejects it, so
        // the TrimStart must stay; deleting it reddens MustParseABomPrefixedBody (verified). No test pins the
        // UTF-16 choice itself, but ReceiveMessageFromQueueCommand's gzip test
        // (SUBSTRING(message_body, 1, 2) = 0x1F8B) is what makes it safe to decode here: a UTF-16LE byte order
        // mark is the byte pair 0xFF 0xFE, not the 0x1F 0x8B gzip magic, so an Error body always passes the
        // receive query through undecompressed.
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

            string decoded;
            try
            {
                decoded = StrictUnicode.GetString(body);
            }
            catch (DecoderFallbackException)
            {
                return Unreadable();
            }

            var document = decoded.TrimStart(ByteOrderMark);

            return TryReadErrorDocument(document, out var code, out var description)
                ? ErrorDescription.Sanitized(code, description)
                : Unreadable();
        }

        private static ErrorDescription Unreadable()
            => ErrorDescription.Sanitized(UnreadableErrorPayloadSentinel, UnreadableErrorPayloadSentinel);

        // INVARIANT: no document type definition is ever processed — DtdProcessing.Prohibit makes a DTD an
        // XmlException. Pinned by MustReturnTheUnreadableSentinelForADocumentThatDeclaresADtd, which switching
        // DtdProcessing to Parse reddens (verified). XmlResolver = null denies external resolution, but no test
        // pins it: the DTD is already refused before a resolver could be consulted, so nulling the setting out
        // reddens nothing (verified) (ADR-0027).
        //
        // INVARIANT: every value this projects is one this code PRODUCES, never one it copies — Code is the
        // invariant-culture rendering of an Int32 parsed from an Error-namespace <Code>, and Description is an
        // Error-namespace <Description>'s content after the printing-category allowlist and the 3000 bound.
        // Pinned by MustRenderTheCodeFromTheParsedInteger, which projecting the raw <Code> content instead of
        // the parsed Int32 reddens; by MustReturnTheUnreadableSentinelForACodeThatIsNotAnInt32, which accepting
        // raw content reddens; and by MustReturnTheUnreadableSentinelForANonErrorDocument, which dropping the
        // NamespaceURI half of the element allowlist reddens (all verified) (ADR-0027).
        //
        // NOTE: the reader deliberately tolerates the rest of the document — trailing content after </Error>,
        // attributes, unknown children, duplicate children (last one wins) and repeated leading byte order
        // marks are all accepted. No test pins that tolerance. It is safe because nothing outside the two
        // allowlisted elements is projected; why an Error body is read at all is in
        // docs/adr/0037-a-terminal-receive-outcome-ends-its-conversation-and-a-deterministic-sql-fault-is-not-retried.md.
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
                    var rawCode = reader.ReadElementContentAsString();
                    code = int.TryParse(rawCode, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedCode)
                        ? parsedCode.ToString(CultureInfo.InvariantCulture)
                        : null;
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

        // INVARIANT: every projected value is bounded and carries only printing characters — anything outside
        // the IsPrinting allowlist collapses to a single space. Pinned by
        // MustNeutraliseEveryNonPrintingCharacterInTheDescription and MustNeutraliseControlCharactersInTheDescription,
        // which adding UnicodeCategory.Control to the allowlist reddens, and by
        // MustTruncateADescriptionLongerThanTheDescriptionBound; returning value unchanged reddens those three
        // (verified) (ADR-0027).
        //
        // MaxCodeLength is unreachable by construction: the only codes that reach Sanitize are an Int32 rendered
        // in the invariant culture (at most 11 characters) and the compile-time sentinel constants, all shorter
        // than the bound. No test pins the code bound.
        //
        // INVARIANT: classification is per RUNE, so a legitimate surrogate pair is judged as the code point it
        // encodes rather than as two lone surrogates. Pinned by MustKeepPrintableTextIncludingNonBmpCharacters,
        // which classifying per char reddens (verified); returning value unchanged leaves it GREEN, so that
        // oracle pins the rune decision only (verified) (ADR-0027).
        //
        // INVARIANT: a surrogate pair is never split by the bound — the budget is spent a whole rune at a time,
        // so a trailing pair that does not fit is dropped entirely rather than truncated to a lone surrogate.
        // No test pins the split itself: MustKeepPrintableTextIncludingNonBmpCharacters drives a non-BMP rune
        // but not one straddling maxLength (ADR-0027).
        private static string Sanitize(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sanitized = new StringBuilder(Math.Min(value.Length, maxLength) + TruncationMarker.Length);
            var remaining = value.AsSpan();
            var keptLength = 0;

            while (!remaining.IsEmpty)
            {
                var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
                if (keptLength + consumed > maxLength)
                {
                    break;
                }

                if (status == OperationStatus.Done && IsPrinting(rune))
                {
                    sanitized.Append(remaining.Slice(0, consumed));
                }
                else
                {
                    sanitized.Append(' ');
                }

                keptLength += consumed;
                remaining = remaining.Slice(consumed);
            }

            if (!remaining.IsEmpty)
            {
                sanitized.Append(TruncationMarker);
            }

            return sanitized.ToString();
        }

        // A POSITIVE allowlist: a rune is projected only when its Unicode category is one that prints. Every
        // other category — Control, Format, PrivateUse, LineSeparator, ParagraphSeparator and OtherNotAssigned
        // — becomes a space without being named, so a code point nobody enumerated cannot reach a log. A lone
        // surrogate never reaches this predicate at all: a Rune cannot hold one, so DecodeFromUtf16 reports a
        // non-Done status and Sanitize spaces it there.
        //
        // RES-A: a peer-sent U+FFFD is OtherSymbol and therefore passes. Left as a recorded residual — after
        // the refusing decode above a U+FFFD can only be one the peer typed, it is bounded to a single visible
        // glyph and cannot forge a log record. Refusing it was considered and rejected: it is a legal
        // character, and the repair path that used to manufacture one no longer exists.
        //
        // RES-B: UnicodeCategory tables are supplied by the runtime, so a code point newly assigned in a later
        // .NET runtime's tables may print on that runtime and become a space on an earlier one. Left as a
        // recorded residual — the divergence is cosmetic, and the categories that decide SAFETY (Control,
        // Format, PrivateUse, LineSeparator, ParagraphSeparator) are stable across the .NET 8 and .NET 10
        // runtimes. Pinning a private category table was considered and rejected: it re-opens the enumeration
        // this allowlist replaced. Tests must therefore use category-stable code points only.
        private static bool IsPrinting(Rune rune)
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.OtherNumber:
                case UnicodeCategory.ConnectorPunctuation:
                case UnicodeCategory.DashPunctuation:
                case UnicodeCategory.OpenPunctuation:
                case UnicodeCategory.ClosePunctuation:
                case UnicodeCategory.InitialQuotePunctuation:
                case UnicodeCategory.FinalQuotePunctuation:
                case UnicodeCategory.OtherPunctuation:
                case UnicodeCategory.MathSymbol:
                case UnicodeCategory.CurrencySymbol:
                case UnicodeCategory.ModifierSymbol:
                case UnicodeCategory.OtherSymbol:
                case UnicodeCategory.SpaceSeparator:
                    return true;
                default:
                    return false;
            }
        }

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
