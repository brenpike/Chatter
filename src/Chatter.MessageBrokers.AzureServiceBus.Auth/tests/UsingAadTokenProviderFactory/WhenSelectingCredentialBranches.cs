using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using System;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth.Tests.UsingAadTokenProviderFactory
{
    public class WhenSelectingCredentialBranches : global::Chatter.Testing.Core.Context
    {
        private const string FullAuthority = "https://login.microsoftonline.com/tenant-id/";
        private const string ExpectedTenantId = "tenant-id";
        private const string SovereignAuthority = "https://login.microsoftonline.us/tenant-id/v2.0";

        private readonly AadTokenProviderFactory _factory = AadTokenProviderFactory.Create("client-id");

        [Fact]
        public void MustReturnClientSecretCredentialForFullDirectoryAuthority()
        {
            var credential = _factory.WithSecret("secret", FullAuthority);

            credential.Should().BeOfType<ClientSecretCredential>();
        }

        // INVARIANT: the tenant id is the first non-empty path segment of the directory authority.
        // Every later segment is authority-endpoint routing (v2.0, oauth2/v2.0/token) and is not part
        // of the tenant id. These assert the resolved tenant actually reaches the credential rather
        // than only that a ClientSecretCredential came back: a suffix-stripping or hardcoded-tenant
        // implementation satisfies a type assertion but fails here.
        [Theory]
        [InlineData("https://login.microsoftonline.com/tenant-id/v2.0", ExpectedTenantId)]
        [InlineData("https://login.microsoftonline.com/tenant-id/v2.0/", ExpectedTenantId)]
        [InlineData("https://login.microsoftonline.com/contoso.onmicrosoft.com/v2.0", "contoso.onmicrosoft.com")]
        [InlineData("https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token", ExpectedTenantId)]
        [InlineData(SovereignAuthority, ExpectedTenantId)]
        public void MustResolveTenantIdFromFirstAuthorityPathSegment(string authority, string expectedTenantId)
        {
            var credential = _factory.WithSecret("secret", authority);

            ReadTenantIdFrom(credential).Should().Be(expectedTenantId);
        }

        [Fact]
        public void MustPreserveSovereignAuthorityHostWhileResolvingTenantId()
        {
            var credential = _factory.WithSecret("secret", SovereignAuthority);

            ReadAuthorityHostFrom(credential).Should().Be(new Uri("https://login.microsoftonline.us/"));
        }

        // INVARIANT: narrowing guard. These authority shapes already resolve to the correct tenant id
        // and must keep doing so. The empty leading segment of the double-slash shape is why the tenant
        // id is the first NON-EMPTY path segment and not Segments[1], which resolves to an empty tenant
        // for that shape. Do not delete these as redundant with the cases above — they pin what must
        // not narrow, not what must widen.
        [Theory]
        [InlineData("https://login.microsoftonline.com//tenant-id/")]
        [InlineData("https://login.microsoftonline.com/tenant-id")]
        [InlineData("https://login.microsoftonline.com/tenant-id/")]
        public void MustResolveTenantIdFromAlreadySupportedAuthorityShapes(string authority)
        {
            var credential = _factory.WithSecret("secret", authority);

            ReadTenantIdFrom(credential).Should().Be(ExpectedTenantId);
        }

        [Fact]
        public void MustThrowWhenAuthorityCarriesNoTenantSegment()
        {
            var build = () => _factory.WithSecret("secret", "https://login.microsoftonline.com/");

            build.Should().Throw<ArgumentNullException>();
        }

        // CHARACTERIZATION: accepted residue of resolving the tenant id as the first non-empty path
        // segment. A non-AAD directory authority prefixes the tenant with a routing segment (B2C
        // /tfp/, ADFS, DSTS), so the first segment is a character-set-valid but wrong tenant id: the
        // credential constructs successfully and the failure moves from construction to token
        // acquisition. Accepted because those authority types cannot authenticate to Azure Service Bus
        // at all — Service Bus RBAC does not honour B2C tokens — so no supported configuration reaches
        // this shape. Pinned so the residue stays visible instead of hidden.
        [Fact]
        public void MustResolveNonAadRoutingSegmentAsTenantId()
        {
            var credential = _factory.WithSecret("secret", "https://login.microsoftonline.com/tfp/tenant/policy");

            ReadTenantIdFrom(credential).Should().Be("tfp");
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

        private static string ReadTenantIdFrom(TokenCredential credential)
        {
            const BindingFlags InternalInstance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

            var tenantId = credential.GetType().GetProperty("TenantId", InternalInstance);
            if (tenantId is null)
            {
                throw new InvalidOperationException($"{credential.GetType().FullName} no longer exposes a 'TenantId' property; this SDK characterization needs updating.");
            }

            return (string)tenantId.GetValue(credential);
        }

        private static Uri ReadAuthorityHostFrom(TokenCredential credential)
        {
            const BindingFlags InternalInstance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

            var client = credential.GetType().GetProperty("Client", InternalInstance)?.GetValue(credential);
            if (client is null)
            {
                throw new InvalidOperationException($"{credential.GetType().FullName} no longer exposes a 'Client' property; this SDK characterization needs updating.");
            }

            var authorityHost = client.GetType().GetProperty("AuthorityHost", InternalInstance);
            if (authorityHost is null)
            {
                throw new InvalidOperationException($"{client.GetType().FullName} no longer exposes an 'AuthorityHost' property; this SDK characterization needs updating.");
            }

            return (Uri)authorityHost.GetValue(client);
        }
    }
}
