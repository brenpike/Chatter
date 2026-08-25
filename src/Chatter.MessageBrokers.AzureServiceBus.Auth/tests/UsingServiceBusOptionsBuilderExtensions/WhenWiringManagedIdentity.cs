using System.Collections.Generic;
using Azure.Identity;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingServiceBusOptionsBuilderExtensions
{
    public class WhenWiringManagedIdentity : global::Chatter.Testing.Core.Context
    {
        private const string ClientId = "client-id";

        // INVARIANT: the end-to-end ServiceBusOptions.TokenCredential round-trip is intentionally
        // NOT re-asserted here because both ServiceBusOptions.TokenCredential (internal set) and
        // ServiceBusOptionsBuilder.Build() are internal to Chatter.MessageBrokers.AzureServiceBus
        // and not visible to this assembly; that round-trip is owned by the ASB module test
        // WhenBuilding.cs. These tests characterize ONLY the public extension surface.
        private static ServiceBusOptionsBuilder CreateBuilder()
            => ServiceBusOptionsBuilder.Create(
                new ServiceCollection(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build());

        [Fact]
        public void MustNotThrowAtRegistrationFromUseAadTokenProviderWithManagedIdentity()
        {
            var builder = CreateBuilder();

            builder.Invoking(b => b.UseAadTokenProviderWithManagedIdentity(ClientId))
                .Should().NotThrow();
        }

        [Fact]
        public void MustReturnSameBuilderFromUseAadTokenProviderWithManagedIdentity()
        {
            var builder = CreateBuilder();

            var result = builder.UseAadTokenProviderWithManagedIdentity(ClientId);

            result.Should().BeSameAs(builder);
        }

        // INVARIANT: the zero-argument call is how a caller asks for the SYSTEM-ASSIGNED managed
        // identity, so clientId must keep its null default. Tests never read or write environment
        // variables to steer that resolution: they are process-global and xUnit parallelises within
        // a collection.
        [Fact]
        public void MustNotThrowWhenCalledWithNoArguments()
        {
            var builder = CreateBuilder();

            builder.Invoking(b => b.UseAadTokenProviderWithManagedIdentity())
                .Should().NotThrow();
        }

        // INVARIANT: AddTokenProvider(Func<TokenCredential>) invokes eagerly at registration, so the
        // opt builder runs before this method returns and capturing it proves the extension reached
        // AadTokenProviderFactory.Create(clientId).WithManagedIdentity(optBuilder) end to end.
        [Fact]
        public void MustHandManagedIdentityCredentialOptionsToOptBuilder()
        {
            var builder = CreateBuilder();
            ManagedIdentityCredentialOptions capturedOptions = null;

            builder.UseAadTokenProviderWithManagedIdentity(ClientId, opts => capturedOptions = opts);

            capturedOptions.Should().NotBeNull();
        }

        [Fact]
        public void MustForwardOptBuilderExactlyOnce()
        {
            var builder = CreateBuilder();
            var invocationCount = 0;

            builder.UseAadTokenProviderWithManagedIdentity(ClientId, _ => invocationCount++);

            invocationCount.Should().Be(1);
        }
    }
}
