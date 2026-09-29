using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Chatter.MessageBrokers.SqlServiceBroker.Configuration
{
    /// <summary>
    /// The transport settings of a <see cref="SqlServiceBrokerOptions"/> instance, read once when the snapshot is taken,
    /// so that a second transport configuration can be recognised as equivalent to the one already registered or refused
    /// with the settings that diverge.
    /// </summary>
    internal sealed class SqlServiceBrokerTransportSettings
    {
        private const string _redactedValue = "(redacted)";
        private const string _nullValue = "(null)";

        private readonly Type _settingsType;
        private readonly IReadOnlyList<TransportSetting> _settings;

        private SqlServiceBrokerTransportSettings(Type settingsType, IReadOnlyList<TransportSetting> settings)
        {
            _settingsType = settingsType;
            _settings = settings;
        }

        /// <summary>
        /// Reads every transport setting of <paramref name="options"/> once. Later changes to <paramref name="options"/>
        /// do not reach the snapshot.
        /// </summary>
        internal static SqlServiceBrokerTransportSettings SnapshotOf(SqlServiceBrokerOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            return SnapshotOfSettings(options);
        }

        /// <summary>
        /// Reads every public readable, non-indexed instance property that <typeparamref name="TSettings"/> declares or
        /// inherits from <paramref name="settings"/> once.
        /// </summary>
        internal static SqlServiceBrokerTransportSettings SnapshotOfSettings<TSettings>(TSettings settings) where TSettings : class
        {
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            // INVARIANT: a snapshot of SqlServiceBrokerOptions holds every public instance property the type declares,
            // because the set is reflected here rather than listed by hand. Oracle: the Theory
            // WhenSnapshotting.MustReportThePropertyAsTheOnlyDivergenceWhenOnlyThatPropertyDiffers, whose MemberData
            // enumerates the public instance properties of SqlServiceBrokerOptions itself, without this predicate.
            // Mutation: narrowing this predicate. Measured by excluding bool properties: the four bool Theory rows and
            // WhenAddingSqlServiceBroker.MustNameEveryDivergingSettingWhenASecondCallIsRefused go red, and nothing else
            // in this test project.
            var transportSettings = typeof(TSettings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .Select(property => new TransportSetting(property.Name,
                                                         property.GetValue(settings),
                                                         property.IsDefined(typeof(SafeToPrintAttribute), inherit: false)))
                .ToArray();
            return new SqlServiceBrokerTransportSettings(typeof(TSettings), Array.AsReadOnly(transportSettings));
        }

        /// <summary>
        /// Returns one <see cref="Divergence"/> per setting whose values differ between <paramref name="registered"/>
        /// and <paramref name="candidate"/>; an empty list means the two are equivalent. Values compare with
        /// <see cref="object.Equals(object, object)"/>.
        /// </summary>
        internal static IReadOnlyList<Divergence> FindDivergences(SqlServiceBrokerTransportSettings registered, SqlServiceBrokerTransportSettings candidate)
        {
            if (registered is null)
            {
                throw new ArgumentNullException(nameof(registered));
            }

            if (candidate is null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            if (registered._settingsType != candidate._settingsType)
            {
                throw new ArgumentException(
                    $"Settings of {candidate._settingsType} cannot be compared with settings of {registered._settingsType}.",
                    nameof(candidate));
            }

            var candidateSettings = candidate._settings.ToDictionary(setting => setting.Name, StringComparer.Ordinal);
            var divergences = new List<Divergence>();
            foreach (var registeredSetting in registered._settings)
            {
                var candidateSetting = candidateSettings[registeredSetting.Name];
                if (!Equals(registeredSetting.Value, candidateSetting.Value))
                {
                    divergences.Add(new Divergence(registeredSetting.Name,
                                                   registeredSetting.RenderValue(),
                                                   candidateSetting.RenderValue()));
                }
            }
            return divergences;
        }

        /// <summary>
        /// A setting whose value differs between the registered and the candidate transport settings.
        /// </summary>
        internal sealed record Divergence(string SettingName, string RegisteredValue, string CandidateValue);

        private sealed record TransportSetting(string Name, object Value, bool IsSafeToPrint)
        {
            // INVARIANT: a value is printed only when its property carries SafeToPrintAttribute; every other value renders
            // as "(redacted)". Oracle: WhenSnapshotting.MustRenderBothValuesOfAPropertyWithoutSafeToPrintAsRedacted, on a
            // test-local settings type. Mutation: ignoring IsSafeToPrint. Measured: that fact,
            // MustRenderBothConnectionStringValuesAsRedactedWhenConnectionStringsDiffer and
            // WhenAddingSqlServiceBroker.MustRefuseASecondCallWhoseConnectionStringDiverges go red, and nothing else in
            // this test project.
            internal string RenderValue()
            {
                if (!IsSafeToPrint)
                {
                    return _redactedValue;
                }

                return Value is null ? _nullValue : Convert.ToString(Value, CultureInfo.InvariantCulture);
            }
        }
    }
}
