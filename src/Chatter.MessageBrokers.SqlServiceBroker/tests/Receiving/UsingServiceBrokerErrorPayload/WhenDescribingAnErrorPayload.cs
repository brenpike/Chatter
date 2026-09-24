using System;
using System.Text;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingServiceBrokerErrorPayload
{
    public class WhenDescribingAnErrorPayload : Testing.Core.Context
    {
        private const string ErrorNamespace = "http://schemas.microsoft.com/SQL/ServiceBroker/Error";

        [Fact]
        public void MustDecodeTheUnicodeErrorDocument()
        {
            var result = ServiceBrokerErrorPayload.Describe(ErrorBody("-8470", "Remote service has been dropped."));

            result.Code.Should().Be("-8470");
            result.Description.Should().Be("Remote service has been dropped.");
        }

        // The projected values are the ELEMENT CONTENT of Code and Description, not a slice of the document:
        // neither carries markup, and the XML escapes the peer's text arrived in are resolved exactly once.
        [Fact]
        public void MustProjectTheCodeAndDescriptionSeparately()
        {
            var result = ServiceBrokerErrorPayload.Describe(ErrorBody("-8470", "a &lt;b&gt; &amp; c"));

            result.Code.Should().Be("-8470");
            result.Description.Should().Be("a <b> & c");
            result.Code.Should().NotContain("<Code>");
            result.Description.Should().NotContain("<Description>");
        }

        // The projected code is RENDERED from an Int32 this code parsed, never copied from the peer's bytes, so
        // a peer's spelling of a number cannot survive into a log.
        [Theory]
        [InlineData("-08470", "-8470")]
        [InlineData("+5", "5")]
        [InlineData("0001", "1")]
        [InlineData("-0", "0")]
        public void MustRenderTheCodeFromTheParsedInteger(string peerCode, string projectedCode)
        {
            var result = ServiceBrokerErrorPayload.Describe(ErrorBody(peerCode, "x"));

            result.Code.Should().Be(projectedCode);
        }

        // A Code that is not an Int32 is not a Service Broker error code, so the whole payload is unreadable
        // rather than partly projected. The leading- and trailing-space rows pin that only a sign is tolerated.
        [Theory]
        [InlineData("abc")]
        [InlineData("1.0")]
        [InlineData("0x10")]
        [InlineData("")]
        [InlineData(" 1")]
        [InlineData("1 ")]
        [InlineData("2147483648")]
        [InlineData("-2147483649")]
        [InlineData("99999999999999999999")]
        public void MustReturnTheUnreadableSentinelForACodeThatIsNotAnInt32(string peerCode)
        {
            var result = ServiceBrokerErrorPayload.Describe(ErrorBody(peerCode, "x"));

            result.Code.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
        }

        // A live Error body is UTF-16LE WITH a byte order mark, which XmlReader rejects unless it is stripped
        // before the parse.
        [Fact]
        public void MustParseABomPrefixedBody()
        {
            var preamble = Encoding.Unicode.GetPreamble();
            var encoded = Encoding.Unicode.GetBytes(ErrorXml("-8470", "Remote service has been dropped."));
            var body = new byte[preamble.Length + encoded.Length];
            Buffer.BlockCopy(preamble, 0, body, 0, preamble.Length);
            Buffer.BlockCopy(encoded, 0, body, preamble.Length, encoded.Length);

            var result = ServiceBrokerErrorPayload.Describe(body);

            result.Code.Should().Be("-8470");
            result.Description.Should().Be("Remote service has been dropped.");
        }

        [Fact]
        public void MustReturnTheSentinelForANullPayload()
        {
            var result = ServiceBrokerErrorPayload.Describe(null);

            result.Code.Should().Be(ServiceBrokerErrorPayload.NoErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.NoErrorPayloadSentinel);
        }

        [Fact]
        public void MustReturnTheSentinelForAnEmptyPayload()
        {
            var result = ServiceBrokerErrorPayload.Describe(Array.Empty<byte>());

            result.Code.Should().Be(ServiceBrokerErrorPayload.NoErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.NoErrorPayloadSentinel);
        }

        // An oversized body is refused BEFORE it is decoded or parsed, so no bounded-parse setting has to hold
        // the line on its own.
        [Fact]
        public void MustReturnTheUnreadableSentinelWhenTheBodyExceedsTheParseBound()
        {
            var body = Encoding.Unicode.GetBytes(
                ErrorXml("1", new string('a', ServiceBrokerErrorPayload.MaxBodyLengthInBytes)));
            body.Length.Should().BeGreaterThan(ServiceBrokerErrorPayload.MaxBodyLengthInBytes);

            var result = ServiceBrokerErrorPayload.Describe(body);

            result.Code.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
        }

        // A value is produced only from an Error-namespace Code and Description. Everything else - a foreign
        // root, the right root in the wrong namespace (or none), junk bytes, a missing child, or markup inside
        // a child - yields nothing to produce from and is refused rather than echoed.
        [Theory]
        [InlineData("<Whatever>surprise</Whatever>")]
        [InlineData("<Error><Code>1</Code><Description>surprise</Description></Error>")]
        [InlineData("<Error xmlns='urn:not-service-broker'><Code>1</Code><Description>surprise</Description></Error>")]
        [InlineData("surprise, not xml at all")]
        [InlineData("<Error xmlns='" + ErrorNamespace + "'><Code>1</Code></Error>")]
        [InlineData("<Error xmlns='" + ErrorNamespace + "'><Code>1</Code><Description><nested>surprise</nested></Description></Error>")]
        public void MustReturnTheUnreadableSentinelForANonErrorDocument(string document)
        {
            var result = ServiceBrokerErrorPayload.Describe(Encoding.Unicode.GetBytes(document));

            result.Code.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
        }

        // An odd-length body cannot be well-formed UTF-16, so it is refused at the decode - the trailing byte is
        // never half-decoded into a replacement character for the parse to judge.
        [Fact]
        public void MustReturnTheUnreadableSentinelForAnOddLengthBody()
        {
            var result = ServiceBrokerErrorPayload.Describe(new byte[] { 0x3C, 0x00, 0x45 });

            result.Code.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
        }

        // The decode REFUSES malformed UTF-16 rather than repairing it. An unpaired surrogate is not rewritten
        // to U+FFFD and echoed, so a peer cannot have broken bytes projected as a replacement glyph.
        [Fact]
        public void MustReturnTheUnreadableSentinelForABodyWithInvalidUtf16()
        {
            var head = Encoding.Unicode.GetBytes(
                "<Error xmlns='" + ErrorNamespace + "'><Code>1</Code><Description>a");
            var unpairedHighSurrogate = new byte[] { 0x00, 0xD8 };
            var tail = Encoding.Unicode.GetBytes("b</Description></Error>");
            var body = new byte[head.Length + unpairedHighSurrogate.Length + tail.Length];
            Buffer.BlockCopy(head, 0, body, 0, head.Length);
            Buffer.BlockCopy(unpairedHighSurrogate, 0, body, head.Length, unpairedHighSurrogate.Length);
            Buffer.BlockCopy(tail, 0, body, head.Length + unpairedHighSurrogate.Length, tail.Length);

            var result = ServiceBrokerErrorPayload.Describe(body);

            result.Code.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
        }

        // A DTD is never processed and an external entity is never resolved: the declaration alone makes the
        // document unreadable, so neither an entity expansion nor a resolved resource can reach a log.
        [Theory]
        [InlineData("<!DOCTYPE Error [<!ENTITY lol 'lol'><!ENTITY lol2 '&lol;&lol;&lol;'>]>"
            + "<Error xmlns='" + ErrorNamespace + "'><Code>1</Code><Description>&lol2;</Description></Error>")]
        [InlineData("<!DOCTYPE Error SYSTEM 'http://127.0.0.1/chatter-should-never-fetch-this.dtd'>"
            + "<Error xmlns='" + ErrorNamespace + "'><Code>1</Code><Description>x</Description></Error>")]
        public void MustReturnTheUnreadableSentinelForADocumentThatDeclaresADtd(string document)
        {
            var result = ServiceBrokerErrorPayload.Describe(Encoding.Unicode.GetBytes(document));

            result.Code.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
            result.Description.Should().NotContain("lol");
        }

        // The peer controls the DESCRIPTION text verbatim, so a control character must never reach the log as
        // itself. XML normalises the CR LF pair to a single line feed before the sanitiser sees it.
        [Fact]
        public void MustNeutraliseControlCharactersInTheDescription()
        {
            var carriageReturn = ((char)0x0D).ToString();
            var lineFeed = ((char)0x0A).ToString();
            var tab = ((char)0x09).ToString();
            var lineSeparator = ((char)0x2028).ToString();

            var result = ServiceBrokerErrorPayload.Describe(
                ErrorBody("1", "a" + carriageReturn + lineFeed + "b" + tab + "c" + lineSeparator + "d"));

            result.Description.Should().Be("a b c d");
            result.Description.Should().NotContain(carriageReturn);
            result.Description.Should().NotContain(lineFeed);
            result.Description.Should().NotContain(tab);
            result.Description.Should().NotContain(lineSeparator);
        }

        // Safety is decided by what a character IS, not by a list of the ones somebody thought to name: a C1
        // control, a zero-width space, a bidi override and a private-use code point are all non-printing, so
        // each collapses to a single space.
        [Fact]
        public void MustNeutraliseEveryNonPrintingCharacterInTheDescription()
        {
            var nextLine = ((char)0x0085).ToString();
            var controlSequenceIntroducer = ((char)0x009B).ToString();
            var zeroWidthSpace = ((char)0x200B).ToString();
            var rightToLeftOverride = ((char)0x202E).ToString();
            var privateUse = ((char)0xE000).ToString();

            var result = ServiceBrokerErrorPayload.Describe(ErrorBody(
                "1",
                "a" + nextLine + "b" + controlSequenceIntroducer + "c" + zeroWidthSpace + "d"
                + rightToLeftOverride + "e" + privateUse + "f"));

            result.Description.Should().Be("a b c d e f");
            result.Description.Should().NotContain(nextLine);
            result.Description.Should().NotContain(controlSequenceIntroducer);
            result.Description.Should().NotContain(zeroWidthSpace);
            result.Description.Should().NotContain(rightToLeftOverride);
            result.Description.Should().NotContain(privateUse);
        }

        // The allowlist admits printing text of any script, including a non-BMP code point that arrives as a
        // surrogate PAIR - a per-character classifier would see two lone surrogates and blank them both.
        [Fact]
        public void MustKeepPrintableTextIncludingNonBmpCharacters()
        {
            var description = "h" + ((char)0x00E9) + "llo " + ((char)0x4E2D) + " "
                              + char.ConvertFromUtf32(0x1F600) + " ok";

            var result = ServiceBrokerErrorPayload.Describe(ErrorBody("1", description));

            result.Description.Should().Be(description);
        }

        // A NUL is not a legal XML 1.0 character in any form, so a body carrying one is refused at the parse
        // rather than neutralised by the sanitiser - either way it never reaches the log as itself.
        [Fact]
        public void MustReturnTheUnreadableSentinelForABodyCarryingANulCharacter()
        {
            var result = ServiceBrokerErrorPayload.Describe(ErrorBody("1", "a" + ((char)0x00) + "b"));

            result.Code.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
            result.Description.Should().Be(ServiceBrokerErrorPayload.UnreadableErrorPayloadSentinel);
        }

        [Fact]
        public void MustTruncateADescriptionLongerThanTheDescriptionBound()
        {
            var result = ServiceBrokerErrorPayload.Describe(
                ErrorBody("1", new string('a', ServiceBrokerErrorPayload.MaxDescriptionLength + 1)));

            result.Description.Should().HaveLength(
                ServiceBrokerErrorPayload.MaxDescriptionLength + ServiceBrokerErrorPayload.TruncationMarker.Length);
            result.Description.Should().EndWith(ServiceBrokerErrorPayload.TruncationMarker);
        }

        private static byte[] ErrorBody(string code, string description)
            => Encoding.Unicode.GetBytes(ErrorXml(code, description));

        private static string ErrorXml(string code, string description)
            => "<Error xmlns='" + ErrorNamespace + "'><Code>" + code + "</Code><Description>"
               + description + "</Description></Error>";
    }
}
