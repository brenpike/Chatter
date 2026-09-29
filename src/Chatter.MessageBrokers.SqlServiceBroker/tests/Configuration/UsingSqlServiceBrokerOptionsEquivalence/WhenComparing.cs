using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Configuration.UsingSqlServiceBrokerOptionsEquivalence
{
    public class WhenComparing : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Chatter;Integrated Security=true";
        private const string MessageBodyType = "application/json; charset=utf-16";

        public static IEnumerable<object[]> PublicReadableProperties()
            => typeof(SqlServiceBrokerOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
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
        [MemberData(nameof(PublicReadableProperties))]
        public void MustReportThePropertyAsTheOnlyDivergenceWhenOnlyThatPropertyDiffers(string propertyName)
        {
            var registered = NewOptions();
            var candidate = NewOptions();
            var property = typeof(SqlServiceBrokerOptions).GetProperty(propertyName);
            property.SetValue(candidate, CreateDivergentValue(property.PropertyType, property.GetValue(candidate)));

            SqlServiceBrokerOptionsEquivalence.FindDivergences(registered, candidate)
                .Should().ContainSingle()
                .Which.PropertyName.Should().Be(propertyName);
        }

        [Fact]
        public void MustNotReportEitherConnectionStringValueWhenConnectionStringsDiffer()
        {
            const string registeredConnectionString = "Server=registered;Password=registered-secret";
            const string candidateConnectionString = "Server=candidate;Password=candidate-secret";
            var registered = NewOptions();
            registered.ConnectionString = registeredConnectionString;
            var candidate = NewOptions();
            candidate.ConnectionString = candidateConnectionString;

            var divergence = SqlServiceBrokerOptionsEquivalence.FindDivergences(registered, candidate).Single();

            new[] { divergence.RegisteredValue, divergence.CandidateValue }
                .Should().NotContain(value => value.Contains("secret"));
        }

        [Fact]
        public void MustReportBothValuesOfADivergingNonCredentialProperty()
        {
            var registered = NewOptions();
            registered.ReceiverTimeoutInMilliseconds = 1000;
            var candidate = NewOptions();
            candidate.ReceiverTimeoutInMilliseconds = 2000;

            SqlServiceBrokerOptionsEquivalence.FindDivergences(registered, candidate)
                .Should().ContainSingle()
                .Which.Should().Be(new SqlServiceBrokerOptionsEquivalence.Divergence(
                    nameof(SqlServiceBrokerOptions.ReceiverTimeoutInMilliseconds), "1000", "2000"));
        }

        [Fact]
        public void MustDistinguishANullValueFromAnEmptyValue()
        {
            var registered = NewOptions();
            registered.MessageBodyType = null;
            var candidate = NewOptions();
            candidate.MessageBodyType = string.Empty;

            var divergence = SqlServiceBrokerOptionsEquivalence.FindDivergences(registered, candidate).Single();

            divergence.RegisteredValue.Should().NotBe(divergence.CandidateValue);
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenRegisteredOptionsNull()
        {
            Action act = () => SqlServiceBrokerOptionsEquivalence.FindDivergences(null, NewOptions());
            act.Should().Throw<ArgumentNullException>().WithParameterName("registered");
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenCandidateOptionsNull()
        {
            Action act = () => SqlServiceBrokerOptionsEquivalence.FindDivergences(NewOptions(), null);
            act.Should().Throw<ArgumentNullException>().WithParameterName("candidate");
        }

        [Fact]
        public void MustReportNoDivergenceForTwoIndependentlyConstructedDefaultInstances()
            => SqlServiceBrokerOptionsEquivalence.FindDivergences(NewOptions(), NewOptions()).Should().BeEmpty();

        [Fact]
        public void MustReportNoDivergenceForTwoInstancesBuiltIdenticallyThroughTheBuilder()
            => SqlServiceBrokerOptionsEquivalence.FindDivergences(BuildConfiguredOptions(), BuildConfiguredOptions())
                .Should().BeEmpty();
    }
}
