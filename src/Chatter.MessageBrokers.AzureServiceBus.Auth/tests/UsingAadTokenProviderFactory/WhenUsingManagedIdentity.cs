using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using System;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingAadTokenProviderFactory
{
    public class WhenUsingManagedIdentity : global::Chatter.Testing.Core.Context
    {
        private const string ClientId = "client-id";

        private readonly AadTokenProviderFactory _factory = AadTokenProviderFactory.Create(ClientId);

        [Fact]
        public void MustReturnManagedIdentityCredential()
        {
            var credential = _factory.WithManagedIdentity();

            credential.Should().BeOfType<ManagedIdentityCredential>();
        }

        // INVARIANT: managed identity must not be requested through a credential chain. A chain
        // selects which arm answers from ambient host state, so an environment-backed service
        // principal can satisfy the request instead of the managed identity the caller asked for.
        // No chain means no ambient arm selection.
        [Fact]
        public void MustNotReturnDefaultAzureCredential()
        {
            var credential = _factory.WithManagedIdentity();

            credential.Should().NotBeOfType<DefaultAzureCredential>();
        }

        [Fact]
        public void MustNotRequireSecretOrAuthorityArguments()
        {
            var build = () => _factory.WithManagedIdentity();

            build.Should().NotThrow();
        }

        [Fact]
        public void MustInvokeOptBuilderExactlyOnce()
        {
            var invocationCount = 0;

            _factory.WithManagedIdentity(_ => invocationCount++);

            invocationCount.Should().Be(1);
        }

        [Fact]
        public void MustResolveSuppliedClientIdToUserAssignedIdentity()
        {
            var identity = AadTokenProviderFactory.ResolveManagedIdentityId(ClientId);

            identity.ToString().Should().Be("ClientId client-id");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustResolveBlankClientIdToSystemAssignedIdentity(string clientId)
        {
            var identity = AadTokenProviderFactory.ResolveManagedIdentityId(clientId);

            identity.ToString().Should().Be("SystemAssigned");
        }

        [Fact]
        public void MustLetOptBuilderConfigureInheritedAuthorityHost()
        {
            var authorityHost = new Uri("https://login.microsoftonline.us/");
            ManagedIdentityCredentialOptions capturedOptions = null;

            _factory.WithManagedIdentity(opts =>
            {
                opts.AuthorityHost = authorityHost;
                capturedOptions = opts;
            });

            capturedOptions.AuthorityHost.Should().Be(authorityHost);
        }

        // CHARACTERIZATION: ManagedIdentityCredentialOptions.ManagedIdentityId is internal to the
        // Azure SDK assembly, so capturing the options in the opt builder cannot observe which
        // identity was requested and every other test here would still pass against a hardcoded
        // identity. Reflection over the SDK's internals is the only way to prove the resolved
        // identity actually reaches the constructed credential. ACCEPTED RESIDUE: this proves the
        // identity handed to the credential, not which managed-identity transport the credential
        // later selects from host state; on a federated-token host the blank case authenticates as
        // the platform-bound workload identity rather than literally system-assigned.
        [Theory]
        [InlineData(ClientId, "ClientId client-id")]
        [InlineData(null, "SystemAssigned")]
        [InlineData("", "SystemAssigned")]
        [InlineData("   ", "SystemAssigned")]
        public void MustPassResolvedIdentityIntoTheCredential(string clientId, string expectedIdentity)
        {
            var credential = AadTokenProviderFactory.Create(clientId).WithManagedIdentity();

            ReadManagedIdentityIdFrom(credential).Should().Be(expectedIdentity);
        }

        private static string ReadManagedIdentityIdFrom(TokenCredential credential)
        {
            const BindingFlags InternalInstance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

            var client = credential.GetType().GetProperty("Client", InternalInstance)?.GetValue(credential);
            if (client is null)
            {
                throw new InvalidOperationException($"{credential.GetType().FullName} no longer exposes a 'Client' property; this SDK characterization needs updating.");
            }

            var identity = client.GetType().GetProperty("ManagedIdentityId", InternalInstance)?.GetValue(client);
            if (identity is null)
            {
                throw new InvalidOperationException($"{client.GetType().FullName} no longer exposes a 'ManagedIdentityId' property; this SDK characterization needs updating.");
            }

            return identity.ToString();
        }
    }
}
