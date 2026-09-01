using System;

namespace Chatter.SqlChangeFeed.Scripts.Sql
{
    /// <summary>
    /// Escapes T-SQL identifiers and single-quoted literals for safe interpolation into emitted script text.
    /// </summary>
    internal static class SqlIdentifier
    {
        /// <summary>
        /// Wraps <paramref name="name"/> in brackets, doubling any embedded closing bracket.
        /// </summary>
        /// <param name="name">The identifier to escape.</param>
        /// <returns>The bracket-quoted identifier.</returns>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or whitespace.</exception>
        public static string Escape(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException($"'{nameof(name)}' cannot be null or whitespace", nameof(name));
            }

            // INVARIANT: a value already beginning with '[' is escaped, never passed through. A
            // pass-through bypass would let a caller-supplied value close the bracket quoting itself.
            return "[" + name.Replace("]", "]]") + "]";
        }

        /// <summary>
        /// Escapes <paramref name="schema"/> and <paramref name="name"/> separately and joins them with a dot.
        /// </summary>
        /// <param name="schema">The schema part.</param>
        /// <param name="name">The object part.</param>
        /// <returns>The dot-joined, bracket-quoted two-part name.</returns>
        /// <exception cref="ArgumentException">Either part is null, empty or whitespace.</exception>
        public static string EscapeQualified(string schema, string name)
            => Escape(schema) + "." + Escape(name);

        /// <summary>
        /// Splits <paramref name="dottedName"/> on dots and escapes each part, so <c>dbo.MyQueue</c> becomes <c>[dbo].[MyQueue]</c>.
        /// </summary>
        /// <param name="dottedName">The dot-separated name.</param>
        /// <returns>The dot-joined, bracket-quoted name.</returns>
        /// <exception cref="ArgumentException"><paramref name="dottedName"/> or any of its parts is null, empty or whitespace.</exception>
        public static string EscapeQualified(string dottedName)
        {
            if (string.IsNullOrWhiteSpace(dottedName))
            {
                throw new ArgumentException($"'{nameof(dottedName)}' cannot be null or whitespace", nameof(dottedName));
            }

            string[] parts = dottedName.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = Escape(parts[i]);
            }

            return string.Join(".", parts);
        }

        /// <summary>
        /// Escapes <paramref name="value"/> for a single-quoted T-SQL literal by doubling each apostrophe once
        /// per nesting level. The surrounding quotes are not returned; the emitting format string carries them.
        /// </summary>
        /// <param name="value">The literal value to escape.</param>
        /// <param name="nestingDepth">How many nested single-quoted layers the literal passes through. Depth 1 doubles each apostrophe, depth 2 quadruples it.</param>
        /// <returns>The escaped literal body.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="nestingDepth"/> is less than 1.</exception>
        public static string QuoteLiteral(string value, int nestingDepth = 1)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (nestingDepth < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(nestingDepth), nestingDepth, $"'{nameof(nestingDepth)}' must be at least 1");
            }

            string quoted = value;
            for (int level = 0; level < nestingDepth; level++)
            {
                quoted = quoted.Replace("'", "''");
            }

            return quoted;
        }
    }
}
