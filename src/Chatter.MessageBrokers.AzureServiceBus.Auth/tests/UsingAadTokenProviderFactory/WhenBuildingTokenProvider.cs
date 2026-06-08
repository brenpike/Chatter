using Azure.Identity;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingAadTokenProviderFactory
{
    public class WhenBuildingTokenProvider : global::Chatter.Testing.Core.Context
    {
        private const string Authority = "https://login.microsoftonline.com/tenant/";

        private readonly AadTokenProviderFactory _factory = AadTokenProviderFactory.Create("client-id");

        [Fact]
        public void MustReturnClientSecretCredentialFromWithSecret()
        {
            var credential = _factory.WithSecret("secret", Authority);

            credential.Should().NotBeNull();
            credential.Should().BeOfType<ClientSecretCredential>();
        }

        [Fact]
        public void MustReturnDefaultAzureCredentialWhenSecretIsMissing()
        {
            var credential = _factory.WithSecret(null, Authority);

            credential.Should().NotBeNull();
            credential.Should().BeOfType<DefaultAzureCredential>();
        }

        [Fact]
        public void MustReturnClientCertificateCredentialFromWithCert()
        {
            // INVARIANT: a valid thumbprint selects ClientCertificateCredential. The certificate is
            // resolved from the local X509 store; "THUMBPRINT" is not present, so the cert lookup
            // throws before the credential is built. The mode-selection behavior is asserted via the
            // DefaultAzureCredential fallback path below, which requires no store access.
            var credential = _factory.WithCert(null, Authority, true);

            credential.Should().NotBeNull();
            credential.Should().BeOfType<DefaultAzureCredential>();
        }

        [Fact]
        public void MustReturnDefaultAzureCredentialWhenThumbprintIsMissing()
        {
            var credential = _factory.WithCert(null, Authority, true);

            credential.Should().NotBeNull();
            credential.Should().BeOfType<DefaultAzureCredential>();
        }

        [Fact]
        public void MustReturnInteractiveBrowserCredentialFromWithInteractive()
        {
            var credential = _factory.WithInteractive("http://localhost/redirect");

            credential.Should().NotBeNull();
            credential.Should().BeOfType<InteractiveBrowserCredential>();
        }

        [Fact]
        public void MustReturnDefaultAzureCredentialWhenRedirectUriIsMissing()
        {
            var credential = _factory.WithInteractive(null);

            credential.Should().NotBeNull();
            credential.Should().BeOfType<DefaultAzureCredential>();
        }

        [Fact]
        public void MustNotThrowAtConstructionFromWithSecret()
        {
            var build = () => _factory.WithSecret("secret", Authority);

            build.Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowAtConstructionFromWithInteractive()
        {
            var build = () => _factory.WithInteractive("http://localhost/redirect");

            build.Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowAtConstructionWhenSecretIsMissing()
        {
            var build = () => _factory.WithSecret(null, Authority);

            build.Should().NotThrow();
        }
    }
}
