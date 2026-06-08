using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingChatterJsonEncoder
{
    // ====================================================================================
    // DIRECT unit tests for ChatterJsonEncoder — the custom JavaScriptEncoder added by the
    // Phase-2 Newtonsoft -> System.Text.Json port.
    //
    // ChatterJsonEncoder is internal (InternalsVisibleTo("Chatter.MessageBrokers.Tests")), so
    // these tests assert against the real ChatterJsonEncoder.Shared instance through the public
    // JsonSerializer surface (JsonSerializer.Serialize(value, ChatterJson.Options)), which wires
    // ChatterJsonEncoder.Shared as its Encoder. Each assertion pins the EXACT serialized string so
    // the encoder's escaping contract is locked independently of the wire-format parity suite.
    //
    // CONTRACT (parity with the prior Newtonsoft.Json wire form):
    //   * ASCII HTML-sensitive chars left LITERAL (UnsafeRelaxedJsonEscaping behavior): < > & ' + /
    //   * Mandatory structural escapes STILL applied: " -> \" and \ -> \\
    //   * ALL non-ASCII (>= 0x80) left LITERAL UTF-8, including BMP (é ü) and astral /
    //     supplementary-plane scalars (emoji 😀 U+1F600) — the CORE new behavior the decorator adds
    //     on top of UnsafeRelaxedJsonEscaping, which would otherwise surrogate-escape astral scalars.
    //
    // OUT OF CONTRACT (asserted only as observed STJ behavior, NOT Newtonsoft parity): C0 control
    // characters (< 0x80) are delegated to the inner encoder, which emits STJ's escapes (e.g. \n).
    // ====================================================================================
    public class WhenEncoding : Testing.Core.Context
    {
        private static string Serialize(string value)
            => JsonSerializer.Serialize(new ValueHolder { prop = value }, ChatterJson.Options);

        private class ValueHolder
        {
            public string prop { get; set; }
        }

        [Fact]
        public void MustLeaveAsciiHtmlSensitiveCharactersLiteral()
        {
            // < > & ' + / are HTML-sensitive and would be \uXXXX-escaped by STJ's DEFAULT encoder.
            // The UnsafeRelaxedJsonEscaping inner encoder (which ChatterJsonEncoder delegates to for
            // ASCII) leaves them literal — this is the wire-format parity behavior with Newtonsoft.
            Serialize("<>&'+/").Should().Be("{\"prop\":\"<>&'+/\"}");
        }

        [Fact]
        public void MustApplyMandatoryStructuralEscapesForQuoteAndBackslash()
        {
            // " and \ are structurally mandatory two-char JSON escapes under every encoder.
            Serialize("\"\\").Should().Be("{\"prop\":\"\\\"\\\\\"}");
        }

        [Fact]
        public void MustLeaveBmpNonAsciiCharactersLiteral()
        {
            // é (U+00E9) and ü (U+00FC) are BMP non-ASCII (>= 0x80) -> left literal UTF-8, not \uXXXX.
            Serialize("éü").Should().Be("{\"prop\":\"éü\"}");
        }

        [Fact]
        public void MustLeaveAstralSupplementaryPlaneScalarLiteral()
        {
            // CORE NEW BEHAVIOR: the astral emoji 😀 (U+1F600) is a supplementary-plane scalar whose
            // UTF-16 form is a surrogate pair. UnsafeRelaxedJsonEscaping ALONE surrogate-escapes it
            // (-> 😀); ChatterJsonEncoder forces every code unit >= 0x80 literal, so both
            // surrogate halves are emitted as the literal 4-byte UTF-8 sequence (F0 9F 98 80).
            var json = Serialize("\U0001F600");

            json.Should().Be("{\"prop\":\"\U0001F600\"}");

            // Pin byte-for-byte: the literal 4-byte UTF-8 encoding is present and NO \u escape appears.
            Encoding.UTF8.GetBytes(json)
                .Should().Equal(Encoding.UTF8.GetBytes("{\"prop\":\"\U0001F600\"}"));
            json.Should().NotContain("\\u");
        }

        [Fact]
        public void MustLeaveFirstNonAsciiBoundaryScalarLiteralButEscapeLastAsciiScalar()
        {
            // Boundary at the 0x80 cut line:
            //   U+0080 is the FIRST non-ASCII scalar (>= 0x80) -> left literal by ChatterJsonEncoder.
            //   U+007F (DEL) is the LAST ASCII scalar (< 0x80) -> delegated to the inner encoder,
            //     which escapes it to the long form  (out of contract; pinned as STJ behavior).
            Serialize("").Should().Be("{\"prop\":\"\"}");
            Serialize("").Should().Be("{\"prop\":\"\\u007F\"}");
        }

        [Fact]
        public void MustDelegateC0ControlEscapingToInnerEncoderOutOfContract()
        {
            // OUT OF CONTRACT: C0 controls (< 0x80) are delegated to UnsafeRelaxedJsonEscaping. This
            // pins the OBSERVED STJ output (\n short escape), NOT Newtonsoft parity — raw control
            // characters do not appear in real brokered-message content and are not a parity surface.
            Serialize("\n").Should().Be("{\"prop\":\"\\n\"}");
        }

        [Fact]
        public void MustExposeSharedInstanceAsTheConfiguredEncoder()
        {
            // ChatterJson.Options is wired with the cached ChatterJsonEncoder.Shared singleton.
            ChatterJson.Options.Encoder.Should().BeSameAs(ChatterJsonEncoder.Shared);
            ChatterJson.Options.Encoder.Should().BeOfType<ChatterJsonEncoder>();
        }

        [Fact]
        public void MustMirrorInnerMaxOutputCharactersPerInputCharacter()
        {
            // The decorator forwards capacity sizing to the inner encoder so the writer allocates
            // identically to UnsafeRelaxedJsonEscaping.
            ChatterJsonEncoder.Shared.MaxOutputCharactersPerInputCharacter
                .Should().Be(JavaScriptEncoder.UnsafeRelaxedJsonEscaping.MaxOutputCharactersPerInputCharacter);
        }

        [Fact]
        public void MustNotEncodeAnyScalarAtOrAboveTheAsciiBoundary()
        {
            // Direct WillEncode contract: every scalar >= 0x80 is never flagged for encoding,
            // including both halves of the U+1F600 surrogate pair (0xD83D, 0xDE00).
            ChatterJsonEncoder.Shared.WillEncode(0x80).Should().BeFalse();
            ChatterJsonEncoder.Shared.WillEncode(0x00E9).Should().BeFalse();
            ChatterJsonEncoder.Shared.WillEncode(0xD83D).Should().BeFalse();
            ChatterJsonEncoder.Shared.WillEncode(0xDE00).Should().BeFalse();
            ChatterJsonEncoder.Shared.WillEncode(0x1F600).Should().BeFalse();
        }

        [Fact]
        public void MustDelegateWillEncodeForAsciiScalarsToInnerEncoder()
        {
            // Below 0x80 the decorator defers to UnsafeRelaxedJsonEscaping: " (0x22) and \ (0x5C)
            // must encode; the relaxed HTML-sensitive chars and plain letters must not.
            var inner = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

            ChatterJsonEncoder.Shared.WillEncode('"').Should().Be(inner.WillEncode('"')).And.Be(true);
            ChatterJsonEncoder.Shared.WillEncode('\\').Should().Be(inner.WillEncode('\\')).And.Be(true);
            ChatterJsonEncoder.Shared.WillEncode('<').Should().Be(inner.WillEncode('<')).And.Be(false);
            ChatterJsonEncoder.Shared.WillEncode('A').Should().Be(inner.WillEncode('A')).And.Be(false);
        }
    }
}
