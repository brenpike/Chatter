using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Chatter.MessageBrokers.SqlServiceBroker.Configuration
{
    /// <summary>
    /// Compares two <see cref="SqlServiceBrokerOptions"/> instances setting by setting, so that a second transport
    /// configuration can be recognised as equivalent to the one already registered or refused with the settings that diverge.
    /// </summary>
    internal static class SqlServiceBrokerOptionsEquivalence
    {
        private const string _redactedValue = "(redacted)";
        private const string _nullValue = "(null)";

        // INVARIANT: the comparison covers every public readable instance property of SqlServiceBrokerOptions,
        // because the set is reflected here rather than listed by hand. Oracle: the Theory
        // WhenComparing.MustReportThePropertyAsTheOnlyDivergenceWhenOnlyThatPropertyDiffers, whose MemberData reflects
        // the same property set independently. Mutation: hard-coding a subset here. Measured by hand-listing six of the
        // eight properties (omitting CompressMessageBody and CleanupOnEndConversation): exactly those two Theory rows
        // go red and every other fact in WhenComparing stays green.
        private static readonly PropertyInfo[] _comparedProperties = typeof(SqlServiceBrokerOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToArray();

        /// <summary>
        /// Returns one <see cref="Divergence"/> per setting whose values differ between <paramref name="registered"/>
        /// and <paramref name="candidate"/>; an empty list means the two are equivalent. Values compare with
        /// <see cref="object.Equals(object, object)"/>. The connection string's values are redacted.
        /// </summary>
        internal static IReadOnlyList<Divergence> FindDivergences(SqlServiceBrokerOptions registered, SqlServiceBrokerOptions candidate)
        {
            if (registered is null)
            {
                throw new ArgumentNullException(nameof(registered));
            }

            if (candidate is null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            var divergences = new List<Divergence>();
            foreach (var property in _comparedProperties)
            {
                var registeredValue = property.GetValue(registered);
                var candidateValue = property.GetValue(candidate);
                if (!Equals(registeredValue, candidateValue))
                {
                    divergences.Add(new Divergence(property.Name,
                                                   FormatValue(property, registeredValue),
                                                   FormatValue(property, candidateValue)));
                }
            }
            return divergences;
        }

        private static string FormatValue(PropertyInfo property, object value)
        {
            if (property.Name == nameof(SqlServiceBrokerOptions.ConnectionString))
            {
                return _redactedValue;
            }

            return value is null ? _nullValue : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A setting whose value differs between the registered and the candidate <see cref="SqlServiceBrokerOptions"/>.
        /// </summary>
        internal sealed record Divergence(string PropertyName, string RegisteredValue, string CandidateValue);
    }
}
