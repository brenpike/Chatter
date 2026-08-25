using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingAadTokenProviderFactory
{
    public class WhenUsingManagedIdentity : global::Chatter.Testing.Core.Context
    {
        private const string ClientId = "client-id";
        private const string UserAssignedIdentityResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity";

        private readonly AadTokenProviderFactory _factory = AadTokenProviderFactory.Create(ClientId);

        [Fact]
        public void MustReturnDefaultAzureCredential()
        {
            var credential = _factory.WithManagedIdentity();

            credential.Should().BeOfType<DefaultAzureCredential>();
        }

        [Fact]
        public void MustNotRequireSecretOrAuthorityArguments()
        {
            var build = () => _factory.WithManagedIdentity();

            build.Should().NotThrow();
        }

        [Fact]
        public void MustPinClientIdToManagedIdentityClientId()
        {
            string capturedClientId = null;

            _factory.WithManagedIdentity(opts => capturedClientId = opts.ManagedIdentityClientId);

            capturedClientId.Should().Be(ClientId);
        }

        [Fact]
        public void MustPinClientIdToWorkloadIdentityClientId()
        {
            string capturedClientId = null;

            _factory.WithManagedIdentity(opts => capturedClientId = opts.WorkloadIdentityClientId);

            capturedClientId.Should().Be(ClientId);
        }

        // INVARIANT: the pin runs before the opt builder, so an explicit assignment wins. The
        // in-callback assertion proves the pin already ran; the post-return assertion on the same
        // options instance proves nothing re-pins it after the callback.
        [Fact]
        public void MustLetOptBuilderOverrideThePinnedClientId()
        {
            const string overrideClientId = "override-client-id";
            DefaultAzureCredentialOptions capturedOptions = null;

            _factory.WithManagedIdentity(opts =>
            {
                opts.ManagedIdentityClientId.Should().Be(ClientId);
                opts.ManagedIdentityClientId = overrideClientId;
                capturedOptions = opts;
            });

            capturedOptions.ManagedIdentityClientId.Should().Be(overrideClientId);
        }

        [Fact]
        public void MustInvokeOptBuilderExactlyOnce()
        {
            var invocationCount = 0;

            _factory.WithManagedIdentity(_ => invocationCount++);

            invocationCount.Should().Be(1);
        }

        // INVARIANT: ManagedIdentityClientId and WorkloadIdentityClientId both default to the
        // AZURE_CLIENT_ID environment variable, so a null assertion would hold only on a host with no
        // Azure tooling configured. Compare against a freshly constructed options instance instead,
        // which carries whatever default this host resolves. Tests never mutate environment variables:
        // they are process-global and xUnit parallelises within a collection.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustLeaveIdentityClientIdsAtTheirDefaultsWhenClientIdIsBlank(string clientId)
        {
            var defaults = new DefaultAzureCredentialOptions();
            DefaultAzureCredentialOptions capturedOptions = null;

            AadTokenProviderFactory.Create(clientId).WithManagedIdentity(opts => capturedOptions = opts);

            capturedOptions.ManagedIdentityClientId.Should().Be(defaults.ManagedIdentityClientId);
            capturedOptions.WorkloadIdentityClientId.Should().Be(defaults.WorkloadIdentityClientId);
        }

        // CHARACTERIZATION: the Azure SDK rejects a managed identity configured by both client id and
        // resource id, and it rejects it eagerly while the credential chain is constructed. That guard
        // sits inside the managed-identity arm, so a chain narrowed away from managed identity (via the
        // AZURE_TOKEN_CREDENTIALS environment variable, or an opt builder setting
        // ExcludeManagedIdentityCredential) never reaches it and never throws. ASSUMED: the SDK treats
        // the pinned client id exactly as a directly assigned one, so probing the SDK under equivalent
        // options reports whether this host builds the arm at all. The alternative -- forcing the arm
        // on by setting environment variables -- is forbidden here: they are process-global and xUnit
        // parallelises within a collection.
        [Fact]
        public void MustThrowWhenOptBuilderAlsoSetsManagedIdentityResourceId()
        {
            var build = () => _factory.WithManagedIdentity(opts => opts.ManagedIdentityResourceId = new ResourceIdentifier(UserAssignedIdentityResourceId));

            if (ManagedIdentityArmRejectsBothIdentifiers())
            {
                build.Should().Throw<ArgumentException>();
            }
            else
            {
                build.Should().NotThrow();
            }
        }

        private static bool ManagedIdentityArmRejectsBothIdentifiers()
        {
            var options = new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = ClientId,
                ManagedIdentityResourceId = new ResourceIdentifier(UserAssignedIdentityResourceId)
            };

            try
            {
                _ = new DefaultAzureCredential(options);
                return false;
            }
            catch (ArgumentException)
            {
                // INVARIANT: the throw IS this probe's observation, not a discarded failure -- it
                // reports that this host builds the managed-identity arm of the credential chain.
                return true;
            }
        }
    }
}
