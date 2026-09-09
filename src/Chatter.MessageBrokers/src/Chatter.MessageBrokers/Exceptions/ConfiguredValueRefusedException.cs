using System;

namespace Chatter.MessageBrokers.Exceptions
{
    /// <summary>
    /// Thrown when an options builder refuses a configured value that the runtime sink reading it cannot run with.
    /// </summary>
    public class ConfiguredValueRefusedException : Exception
    {
        private const string _refusedMessage = "A configured value was refused.";

        public ConfiguredValueRefusedException(string optionName, object refusedValue, string requiredBound, string configurationPath)
            : base(BuildRefusalMessage(optionName, refusedValue, requiredBound, configurationPath))
        {
            OptionName = optionName;
            RefusedValue = refusedValue;
            RequiredBound = requiredBound;
            ConfigurationPath = configurationPath;
        }

        /// <summary>
        /// The options property whose configured value was refused, qualified by its options type.
        /// </summary>
        public string OptionName { get; }

        /// <summary>
        /// The configured value that was refused.
        /// </summary>
        public object RefusedValue { get; }

        /// <summary>
        /// The bound the value had to satisfy, stated in terms of the sink the bound was derived from.
        /// </summary>
        public string RequiredBound { get; }

        /// <summary>
        /// The path of the configuration section the refusing builder resolved, or null when it has none.
        /// </summary>
        public string ConfigurationPath { get; }

        // INVARIANT: the configured path is named only when the builder actually resolved a section. A builder
        // retargeted onto a custom section name has a path the documented section constant does not describe, and a
        // nested builder composed by a parent has no section of its own at all, so neither may be handed a
        // fabricated one - the option name still identifies the property in every case.
        private static string BuildRefusalMessage(string optionName, object refusedValue, string requiredBound, string configurationPath)
            => string.IsNullOrWhiteSpace(configurationPath)
                ? $"{_refusedMessage} '{optionName}' was configured as '{refusedValue}' and must be {requiredBound}."
                : $"{_refusedMessage} '{optionName}' was configured as '{refusedValue}' at '{configurationPath}' and must be {requiredBound}.";
    }
}
