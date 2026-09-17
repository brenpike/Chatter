using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Options.UsingServiceBusOptions
{
    // ServiceBusOptions is registered as a DI singleton, and ConnectionString is the one
    // secret-bearing member of that type. RetryOptions and TokenCredential are already
    // [JsonIgnore]d; ConnectionString must match its two siblings so a whole-object dump (a
    // diagnostics endpoint, a startup log) cannot leak the SharedAccessKey it typically carries
    // (#375).
    public class WhenSerializingOptions
    {
        private const string _sectionName = "Chatter:Infrastructure:AzureServiceBus";
        private const string _connectionStringWithSecret =
            "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=SECRETVALUE";

        [Fact]
        public void MustNotSerializeConnectionStringOrItsSecretValue()
        {
            var options = new ServiceBusOptions { ConnectionString = _connectionStringWithSecret };

            var json = JsonSerializer.Serialize(options);

            json.Should().NotContain(nameof(ServiceBusOptions.ConnectionString));
            json.Should().NotContain("SECRETVALUE");
        }

        [Fact]
        public void MustStillBindConnectionStringFromConfiguration()
        {
            // REGRESSION GUARD: [JsonIgnore] gates System.Text.Json only. The
            // Microsoft.Extensions.Configuration binder ServiceBusOptionsBuilder.Build() uses
            // (IConfigurationSection.Bind) does not consult it, so a host binding
            // ConnectionString from configuration is unaffected by this change.
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{_sectionName}:ConnectionString"] = _connectionStringWithSecret
                })
                .Build();
            var options = new ServiceBusOptions();

            config.GetSection(_sectionName).Bind(options);

            options.ConnectionString.Should().Be(_connectionStringWithSecret);
        }
    }
}
