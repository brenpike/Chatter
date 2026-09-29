using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Configuration.UsingSqlServiceBrokerTransportSettings
{
    public class WhenSnapshotting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Chatter;Integrated Security=true";
        private const string MessageBodyType = "application/json; charset=utf-16";
        private const string RedactedValue = "(redacted)";

        private sealed class FakeTransportSettings
        {
            public string UnmarkedSetting { get; set; }

            [SafeToPrint]
            public string MarkedSetting { get; set; }
        }

        public static IEnumerable<object[]> PublicInstanceProperties()
            => typeof(SqlServiceBrokerOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => new object[] { property.Name });

        private static SqlServiceBrokerOptions NewOptions()
            => new SqlServiceBrokerOptions(new string(ConnectionString.AsSpan()), new string(MessageBodyType.AsSpan()));

        private static SqlServiceBrokerOptions BuildConfiguredOptions()
            => new SqlServiceBrokerOptionsBuilder(new ServiceCollection())
                .AddSqlServiceBrokerOptions(new string(ConnectionString.AsSpan()))
                .WithReceiverTimeout(5000)
                .WithConversationLifetime(600)
                .UseConversationEncryption()
                .WithConversationCleanup()
                .EndConversationAfterDispatch(false)
                .Build();

        private static IReadOnlyList<SqlServiceBrokerTransportSettings.Divergence> FindDivergencesBetween(
            SqlServiceBrokerOptions registered, SqlServiceBrokerOptions candidate)
            => SqlServiceBrokerTransportSettings.FindDivergences(SqlServiceBrokerTransportSettings.SnapshotOf(registered),
                                                                 SqlServiceBrokerTransportSettings.SnapshotOf(candidate));

        private static object CreateDivergentValue(Type propertyType, object currentValue)
        {
            if (propertyType == typeof(string))
            {
                return (string)currentValue + "-divergent";
            }

            if (propertyType == typeof(int))
            {
                return (int)currentValue + 1;
            }

            if (propertyType == typeof(bool))
            {
                return !(bool)currentValue;
            }

            throw new InvalidOperationException(
                $"No divergent-value generator for {propertyType}; add one so this property stays covered.");
        }

        [Theory]
        [MemberData(nameof(PublicInstanceProperties))]
        public void MustReportThePropertyAsTheOnlyDivergenceWhenOnlyThatPropertyDiffers(string propertyName)
        {
            var registered = NewOptions();
            var candidate = NewOptions();
            var property = typeof(SqlServiceBrokerOptions).GetProperty(propertyName);
            property.SetValue(candidate, CreateDivergentValue(property.PropertyType, property.GetValue(candidate)));

            FindDivergencesBetween(registered, candidate)
                .Should().ContainSingle()
                .Which.SettingName.Should().Be(propertyName);
        }

        [Fact]
        public void MustRenderBothValuesOfAPropertyWithoutSafeToPrintAsRedacted()
        {
            var registered = new FakeTransportSettings { UnmarkedSetting = "registered-value" };
            var candidate = new FakeTransportSettings { UnmarkedSetting = "candidate-value" };

            SqlServiceBrokerTransportSettings.FindDivergences(SqlServiceBrokerTransportSettings.SnapshotOfSettings(registered),
                                                              SqlServiceBrokerTransportSettings.SnapshotOfSettings(candidate))
                .Should().ContainSingle()
                .Which.Should().Be(new SqlServiceBrokerTransportSettings.Divergence(
                    nameof(FakeTransportSettings.UnmarkedSetting), RedactedValue, RedactedValue));
        }

        [Fact]
        public void MustRenderBothValuesOfAPropertyMarkedSafeToPrint()
        {
            var registered = new FakeTransportSettings { MarkedSetting = "registered-value" };
            var candidate = new FakeTransportSettings { MarkedSetting = "candidate-value" };

            SqlServiceBrokerTransportSettings.FindDivergences(SqlServiceBrokerTransportSettings.SnapshotOfSettings(registered),
                                                              SqlServiceBrokerTransportSettings.SnapshotOfSettings(candidate))
                .Should().ContainSingle()
                .Which.Should().Be(new SqlServiceBrokerTransportSettings.Divergence(
                    nameof(FakeTransportSettings.MarkedSetting), "registered-value", "candidate-value"));
        }

        [Fact]
        public void MustRenderBothConnectionStringValuesAsRedactedWhenConnectionStringsDiffer()
        {
            var registered = NewOptions();
            registered.ConnectionString = "Server=registered;Password=registered-secret";
            var candidate = NewOptions();
            candidate.ConnectionString = "Server=candidate;Password=candidate-secret";

            FindDivergencesBetween(registered, candidate)
                .Should().ContainSingle()
                .Which.Should().Be(new SqlServiceBrokerTransportSettings.Divergence(
                    nameof(SqlServiceBrokerOptions.ConnectionString), RedactedValue, RedactedValue));
        }

        [Fact]
        public void MustReportBothValuesOfADivergingNonCredentialProperty()
        {
            var registered = NewOptions();
            registered.ReceiverTimeoutInMilliseconds = 1000;
            var candidate = NewOptions();
            candidate.ReceiverTimeoutInMilliseconds = 2000;

            FindDivergencesBetween(registered, candidate)
                .Should().ContainSingle()
                .Which.Should().Be(new SqlServiceBrokerTransportSettings.Divergence(
                    nameof(SqlServiceBrokerOptions.ReceiverTimeoutInMilliseconds), "1000", "2000"));
        }

        [Fact]
        public void MustDistinguishANullValueFromAnEmptyValue()
        {
            var registered = NewOptions();
            registered.MessageBodyType = null;
            var candidate = NewOptions();
            candidate.MessageBodyType = string.Empty;

            var divergence = FindDivergencesBetween(registered, candidate).Single();

            divergence.RegisteredValue.Should().NotBe(divergence.CandidateValue);
        }

        [Fact]
        public void MustKeepTheValuesItReadWhenTheOptionsChangeAfterTheSnapshot()
        {
            var options = NewOptions();
            var snapshot = SqlServiceBrokerTransportSettings.SnapshotOf(options);
            options.ReceiverTimeoutInMilliseconds = 1000;

            SqlServiceBrokerTransportSettings.FindDivergences(snapshot, SqlServiceBrokerTransportSettings.SnapshotOf(NewOptions()))
                .Should().BeEmpty();
        }

        [Fact]
        public void MustRefuseToCompareSnapshotsOfDifferentSettingsTypes()
        {
            Action act = () => SqlServiceBrokerTransportSettings.FindDivergences(
                SqlServiceBrokerTransportSettings.SnapshotOf(NewOptions()),
                SqlServiceBrokerTransportSettings.SnapshotOfSettings(new FakeTransportSettings()));
            act.Should().Throw<ArgumentException>().WithParameterName("candidate");
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenOptionsNull()
        {
            Action act = () => SqlServiceBrokerTransportSettings.SnapshotOf(null);
            act.Should().Throw<ArgumentNullException>().WithParameterName("options");
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenSettingsNull()
        {
            Action act = () => SqlServiceBrokerTransportSettings.SnapshotOfSettings<FakeTransportSettings>(null);
            act.Should().Throw<ArgumentNullException>().WithParameterName("settings");
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenRegisteredSnapshotNull()
        {
            Action act = () => SqlServiceBrokerTransportSettings.FindDivergences(null, SqlServiceBrokerTransportSettings.SnapshotOf(NewOptions()));
            act.Should().Throw<ArgumentNullException>().WithParameterName("registered");
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenCandidateSnapshotNull()
        {
            Action act = () => SqlServiceBrokerTransportSettings.FindDivergences(SqlServiceBrokerTransportSettings.SnapshotOf(NewOptions()), null);
            act.Should().Throw<ArgumentNullException>().WithParameterName("candidate");
        }

        [Fact]
        public void MustReportNoDivergenceForTwoIndependentlyConstructedDefaultInstances()
            => FindDivergencesBetween(NewOptions(), NewOptions()).Should().BeEmpty();

        [Fact]
        public void MustReportNoDivergenceForTwoInstancesBuiltIdenticallyThroughTheBuilder()
            => FindDivergencesBetween(BuildConfiguredOptions(), BuildConfiguredOptions()).Should().BeEmpty();
    }
}
