using System.Text;

namespace Chatter.CQRS.DependencyInjection
{
    public class NamespaceSelectorBuilder
    {
        private readonly StringBuilder _namespaceSelectorBuilder;

        private NamespaceSelectorBuilder() => _namespaceSelectorBuilder = new StringBuilder();

        public static NamespaceSelectorBuilder New() => new NamespaceSelectorBuilder();

        /// <summary>
        /// Appends a string literal to the namespace selector
        /// </summary>
        public NamespaceSelectorBuilder Append(string selectorValue)
        {
            _namespaceSelectorBuilder.Append(selectorValue);
            return this;
        }

        /// <summary>
        /// Appends a '*' to the namespace selector which acts as a wildcard for multiple symbols
        /// </summary>
        public NamespaceSelectorBuilder AppendWildcard()
        {
            _namespaceSelectorBuilder.Append("*");
            return this;
        }

        /// <summary>
        /// Appends a '?' to the namespace selector which acts as a wildcard for a single symbol
        /// </summary>
        public NamespaceSelectorBuilder AppendSymbolWildcard()
        {
            _namespaceSelectorBuilder.Append("?");
            return this;
        }

        /// <summary>
        /// Appends a backslash to the namespace selector that acts as an escape character
        /// </summary>
        public NamespaceSelectorBuilder AppendEscape()
        {
            _namespaceSelectorBuilder.Append(@"\");
            return this;
        }

        public string Build() => _namespaceSelectorBuilder.ToString();
    }
}
