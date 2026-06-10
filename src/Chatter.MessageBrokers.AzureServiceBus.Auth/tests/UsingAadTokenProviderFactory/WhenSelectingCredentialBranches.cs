using Azure.Identity;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingAadTokenProviderFactory
{
    public class WhenSelectingCredentialBranches : global::Chatter.Testing.Core.Context
    {
        private const string FullAuthority = "https://login.microsoftonline.com/tenant-id/";

        private readonly AadTokenProviderFactory _factory = AadTokenProviderFactory.Create("client-id");

        [Fact]
        public void MustReturnClientSecretCredentialForFullDirectoryAuthority()
        {
            var credential = _factory.WithSecret("secret", FullAuthority);

            credential.Should().BeOfType<ClientSecretCredential>();
        }

        // CHARACTERIZATION: ParseAuthority yields a null tenant id for a null, empty, whitespace, or
        // non-absolute authority (no parseable first path segment). The Azure.Identity
        // ClientSecretCredential constructor rejects a null tenant id, so WithSecret throws
        // ArgumentNullException ("Tenant id cannot be null") for these inputs rather than returning a
        // credential. Pinned as-is. (The delegation's expectation of a no-throw ClientSecretCredential
        // for these authority values does not match actual SDK behavior; see worker report.)
        [Fact]
        public void MustThrowWhenAuthorityIsNull()
        {
            var build = () => _factory.WithSecret("secret", null);

            build.Should().Throw<ArgumentNullException>();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenAuthorityIsEmptyOrWhitespace(string authority)
        {
            var build = () => _factory.WithSecret("secret", authority);

            build.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenAuthorityIsNotAbsolute()
        {
            var build = () => _factory.WithSecret("secret", "not-an-absolute-uri");

            build.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustInvokeOptBuilderOnceFromWithSecretFallback()
        {
            var invocationCount = 0;

            _factory.WithSecret(null, FullAuthority, _ => invocationCount++);

            invocationCount.Should().Be(1);
        }

        [Fact]
        public void MustInvokeOptBuilderOnceFromWithCertFallback()
        {
            var invocationCount = 0;

            _factory.WithCert(null, FullAuthority, true, _ => invocationCount++);

            invocationCount.Should().Be(1);
        }

        [Fact]
        public void MustInvokeOptBuilderOnceFromWithInteractiveFallback()
        {
            var invocationCount = 0;

            _factory.WithInteractive(null, _ => invocationCount++);

            invocationCount.Should().Be(1);
        }

        [Fact]
        public void MustReturnInteractiveBrowserCredentialForValidRedirectUri()
        {
            var credential = _factory.WithInteractive("http://localhost/redirect");

            credential.Should().BeOfType<InteractiveBrowserCredential>();
        }

        // INVARIANT: a non-existent thumbprint never resolves to a certificate, so WithCert always
        // throws before a ClientCertificateCredential is built. GetCertificate opens the X509 store and
        // either fails to find the cert (ArgumentException) or, on platforms where the requested store
        // location cannot be opened (e.g. the Unix LocalMachine/My store), throws a
        // CryptographicException from the store open itself. Assert the broad throw guarantee that holds
        // on all platforms rather than a single concrete exception type.
        [Fact]
        public void MustThrowForNonExistentCertThumbprint()
        {
            var build = () => _factory.WithCert("NONEXISTENT-THUMBPRINT", FullAuthority, true);

            build.Should().Throw<Exception>();
        }
    }
}
