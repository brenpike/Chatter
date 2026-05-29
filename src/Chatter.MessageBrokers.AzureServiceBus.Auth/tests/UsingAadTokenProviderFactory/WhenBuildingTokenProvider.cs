using FluentAssertions;
using Microsoft.Azure.ServiceBus.Primitives;
using System;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingAadTokenProviderFactory
{
    public class WhenBuildingTokenProvider : global::Chatter.Testing.Core.Context
    {
        private const string Authority = "https://login.microsoftonline.com/tenant/";

        private readonly AadTokenProviderFactory _factory = AadTokenProviderFactory.Create("client-id");

        // INVARIANT: TokenProvider.Authority has no public accessor; reading via reflection.
        // Tries the protected property declared on TokenProvider first; falls back to walking
        // BaseType chain for a private field named "authority" if the property is absent.
        private static string ReadAuthority(object provider)
        {
            var tokenProviderType = typeof(TokenProvider);
            var prop = tokenProviderType.GetProperty(
                "Authority",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (prop != null)
                return (string)prop.GetValue(provider);

            var currentType = provider.GetType();
            while (currentType != null)
            {
                var field = currentType.GetField(
                    "authority",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return (string)field.GetValue(provider);
                currentType = currentType.BaseType;
            }

            throw new InvalidOperationException(
                $"Neither a property 'Authority' on TokenProvider nor a field 'authority' found on {provider.GetType().FullName} hierarchy.");
        }

        [Fact]
        public void MustReturnNonNullProviderFromWithSecret()
        {
            var provider = _factory.WithSecret("secret", Authority);

            provider.Should().NotBeNull();
            provider.Should().BeOfType<AzureActiveDirectoryTokenProvider>();
        }

        [Fact]
        public void MustPassAuthorityThroughFromWithSecret()
        {
            var provider = _factory.WithSecret("secret", Authority);

            ReadAuthority(provider).Should().Be(Authority);
        }

        [Fact]
        public void MustCoerceNullAuthorityToEmptyStringFromWithSecret()
        {
            var provider = _factory.WithSecret("secret", null);

            ReadAuthority(provider).Should().Be("");
        }

        [Fact]
        public void MustReturnNonNullProviderFromWithCert()
        {
            var provider = _factory.WithCert("THUMBPRINT", Authority, true);

            provider.Should().NotBeNull();
            provider.Should().BeOfType<AzureActiveDirectoryTokenProvider>();
        }

        [Fact]
        public void MustPassAuthorityThroughFromWithCert()
        {
            var provider = _factory.WithCert("THUMBPRINT", Authority, true);

            ReadAuthority(provider).Should().Be(Authority);
        }

        [Fact]
        public void MustCoerceNullAuthorityToEmptyStringFromWithCert()
        {
            var provider = _factory.WithCert("THUMBPRINT", null, true);

            ReadAuthority(provider).Should().Be("");
        }

        [Fact]
        public void MustReturnNonNullProviderFromWithInteractive()
        {
            var provider = _factory.WithInteractive("https://localhost/redirect");

            provider.Should().NotBeNull();
            provider.Should().BeOfType<AzureActiveDirectoryTokenProvider>();
        }

        [Fact]
        public void MustHardcodeAuthorityToEmptyStringFromWithInteractive()
        {
            var provider = _factory.WithInteractive("https://localhost/redirect");

            // INVARIANT: WithInteractive always sets authority to "" regardless of input,
            // unlike WithSecret/WithCert which honor a caller-supplied authority via (authority ?? "").
            ReadAuthority(provider).Should().Be("");
        }

        [Fact]
        public void MustNotInvokeAuthCallbackAtConstructionFromWithSecret()
        {
            Action build = () => _factory.WithSecret("secret", Authority);

            build.Should().NotThrow();
        }

        [Fact]
        public void MustNotInvokeAuthCallbackAtConstructionFromWithCert()
        {
            Action build = () => _factory.WithCert("THUMBPRINT", Authority, true);

            build.Should().NotThrow();
        }

        [Fact]
        public void MustNotInvokeAuthCallbackAtConstructionFromWithInteractive()
        {
            Action build = () => _factory.WithInteractive("https://localhost/redirect");

            build.Should().NotThrow();
        }
    }
}
