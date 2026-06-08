using System.Collections.Generic;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingServiceBusOptionsBuilderExtensions
{
    public class WhenWiringTokenProvider : global::Chatter.Testing.Core.Context
    {
        private const string ClientId = "client-id";
        private const string Secret = "secret";
        // INVARIANT: WithCert resolves the certificate from the local X509 store eagerly when the
        // Func fires at registration. A real thumbprint would require a matching cert in the store,
        // so these wiring tests use the missing-thumbprint path (DefaultAzureCredential fallback),
        // which exercises the same eager-Func registration without store access.
        private const string Thumbprint = null;
        private const string Authority = "https://login.microsoftonline.com/tenant/";
        private const string RedirectUri = "https://localhost/redirect";

        // INVARIANT: the end-to-end ServiceBusOptions.TokenCredential round-trip is intentionally
        // NOT re-asserted here because both ServiceBusOptions.TokenCredential (internal set) and
        // ServiceBusOptionsBuilder.Build() are internal to Chatter.MessageBrokers.AzureServiceBus
        // and not visible to this assembly; that round-trip is owned by the ASB module test
        // WhenBuilding.cs. These tests characterize ONLY the public extension surface: that
        // registration invokes the eager Func building an Azure.Core.TokenCredential, and that the
        // fluent contract returns the same builder instance.
        private static ServiceBusOptionsBuilder CreateBuilder()
            => ServiceBusOptionsBuilder.Create(
                new ServiceCollection(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build());

        [Fact]
        public void MustNotThrowAtRegistrationFromUseAadTokenProviderWithSecret()
        {
            var builder = CreateBuilder();

            builder.Invoking(b => b.UseAadTokenProviderWithSecret(ClientId, Secret, Authority))
                .Should().NotThrow();
        }

        [Fact]
        public void MustReturnSameBuilderFromUseAadTokenProviderWithSecret()
        {
            var builder = CreateBuilder();

            var result = builder.UseAadTokenProviderWithSecret(ClientId, Secret, Authority);

            result.Should().BeSameAs(builder);
        }

        [Fact]
        public void MustNotThrowAtRegistrationFromUseAadTokenProviderWithCert()
        {
            var builder = CreateBuilder();

            builder.Invoking(b => b.UseAadTokenProviderWithCert(ClientId, Thumbprint, Authority, validCertsOnly: true))
                .Should().NotThrow();
        }

        [Fact]
        public void MustReturnSameBuilderFromUseAadTokenProviderWithCert()
        {
            var builder = CreateBuilder();

            var result = builder.UseAadTokenProviderWithCert(ClientId, Thumbprint, Authority, validCertsOnly: true);

            result.Should().BeSameAs(builder);
        }

        [Fact]
        public void MustNotThrowAtRegistrationFromUseAadTokenProviderInteractively()
        {
            var builder = CreateBuilder();

            builder.Invoking(b => b.UseAadTokenProviderInteractively(ClientId, RedirectUri))
                .Should().NotThrow();
        }

        [Fact]
        public void MustReturnSameBuilderFromUseAadTokenProviderInteractively()
        {
            var builder = CreateBuilder();

            var result = builder.UseAadTokenProviderInteractively(ClientId, RedirectUri);

            result.Should().BeSameAs(builder);
        }
    }
}
