using System;
using System.Text.Encodings.Web;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// A <see cref="JavaScriptEncoder"/> that produces byte-for-byte parity with the prior
    /// Newtonsoft.Json wire format. It decorates <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>:
    /// the inner encoder supplies the ASCII relaxation (+, &lt;, &gt;, &amp;, ', / left literal) and the
    /// mandatory structural escapes (", \, C0 controls), while this decorator forces ALL non-ASCII
    /// scalars — including astral / supplementary-plane scalars such as emoji (😀 U+1F600) — to be
    /// emitted as literal UTF-8 rather than \uXXXX surrogate escapes.
    /// </summary>
    /// <remarks>
    /// INVARIANT: every code unit >= 0x80 is left unencoded. Both halves of a UTF-16 surrogate pair
    /// are >= 0x80, so the per-code-unit "skip when >= 0x80" rule below is safe for astral scalars —
    /// neither half is ever flagged for encoding, so the writer emits the literal UTF-8 sequence.
    /// </remarks>
    internal sealed class ChatterJsonEncoder : JavaScriptEncoder
    {
        internal static readonly ChatterJsonEncoder Shared = new ChatterJsonEncoder();

        private readonly JavaScriptEncoder _inner = UnsafeRelaxedJsonEscaping;

        public override int MaxOutputCharactersPerInputCharacter
            => _inner.MaxOutputCharactersPerInputCharacter;

        public override bool WillEncode(int unicodeScalar)
        {
            if (unicodeScalar >= 0x80)
            {
                return false;
            }

            return _inner.WillEncode(unicodeScalar);
        }

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (int i = 0; i < textLength; i++)
            {
                char current = text[i];
                if (current >= 0x80)
                {
                    continue;
                }

                if (_inner.WillEncode(current))
                {
                    return i;
                }
            }

            return -1;
        }

        public override int FindFirstCharacterToEncodeUtf8(ReadOnlySpan<byte> utf8Text)
        {
            for (int i = 0; i < utf8Text.Length; i++)
            {
                byte current = utf8Text[i];
                if (current >= 0x80)
                {
                    continue;
                }

                if (_inner.WillEncode(current))
                {
                    return i;
                }
            }

            return -1;
        }

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
            => _inner.TryEncodeUnicodeScalar(unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);
    }
}
