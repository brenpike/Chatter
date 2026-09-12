using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts
{
    internal static class SqlIdentifier
    {
        // INVARIANT: quoting parses the value back to its raw form first, so Quote(Quote(x)) == Quote(x)
        // and a value that is not a well-formed quoted identifier is escaped whole rather than trusted.
        public static string Quote(string value)
        {
            var raw = Unquote(value);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("A SQL identifier cannot be null, empty or whitespace.", nameof(value));
            }

            return "[" + raw.Replace("]", "]]") + "]";
        }

        public static string QuoteMultiPart(string value)
        {
            if (value is null)
            {
                throw new ArgumentException("A SQL identifier cannot be null, empty or whitespace.", nameof(value));
            }

            var parts = new List<string>();
            var partStart = 0;
            var insideBrackets = false;
            var index = 0;
            while (index < value.Length)
            {
                var current = value[index];
                if (insideBrackets)
                {
                    // INVARIANT: "]]" is an escaped bracket, not a terminator, so the scan stays inside the part.
                    if (current == ']' && index + 1 < value.Length && value[index + 1] == ']')
                    {
                        index++;
                    }
                    else if (current == ']')
                    {
                        insideBrackets = false;
                    }
                }
                else if (current == '[')
                {
                    insideBrackets = true;
                }
                else if (current == '.')
                {
                    parts.Add(Quote(value.Substring(partStart, index - partStart)));
                    partStart = index + 1;
                }

                index++;
            }

            parts.Add(Quote(value.Substring(partStart)));

            return string.Join(".", parts);
        }

        public static string Unquote(string value)
        {
            if (value is null || !IsWellFormedQuoted(value))
            {
                return value;
            }

            return value.Substring(1, value.Length - 2).Replace("]]", "]");
        }

        private static bool IsWellFormedQuoted(string value)
        {
            if (value.Length < 2 || value[0] != '[' || value[value.Length - 1] != ']')
            {
                return false;
            }

            var lastInteriorIndex = value.Length - 2;
            for (var index = 1; index <= lastInteriorIndex; index++)
            {
                if (value[index] != ']')
                {
                    continue;
                }

                if (index == lastInteriorIndex || value[index + 1] != ']')
                {
                    return false;
                }

                index++;
            }

            return true;
        }
    }
}
