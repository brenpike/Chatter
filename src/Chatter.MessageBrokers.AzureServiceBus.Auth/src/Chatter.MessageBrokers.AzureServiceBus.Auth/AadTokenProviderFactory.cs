using Azure.Core;
using Azure.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth
{
    public class AadTokenProviderFactory
    {
        private readonly string _clientId;

        public static AadTokenProviderFactory Create(string clientId) => new AadTokenProviderFactory(clientId);

        private AadTokenProviderFactory(string clientId)
        {
            _clientId = clientId;
        }

        /// <summary>
        /// Creates a <see cref="TokenCredential"/> using a client secret. If no client secret is provided a <see cref="DefaultAzureCredential"/> is returned.
        /// </summary>
        /// <param name="clientSecret">The client secret to use to authenticate with Azure AD</param>
        /// <param name="authority">A URL that indicates a directory to request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/. The tenant id is the first non-empty path segment and any deeper segments are ignored, so a /v2.0-suffixed issuer URL copied out of the Azure portal is accepted. The scheme+host becomes the credential's <see cref="Azure.Identity.TokenCredentialOptions.AuthorityHost"/>.</param>
        /// <returns>A <see cref="TokenCredential"/>: a <see cref="ClientSecretCredential"/> when a secret is supplied, otherwise a <see cref="DefaultAzureCredential"/>.</returns>
        public TokenCredential WithSecret(string clientSecret, string authority, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            if (string.IsNullOrWhiteSpace(clientSecret))
            {
                return BuildDefaultAzureCredential(optBuilder);
            }

            var (tenantId, authorityHost) = ParseAuthority(authority);
            var options = new ClientSecretCredentialOptions();
            ApplyAuthorityHost(options, authorityHost);
            return new ClientSecretCredential(tenantId, _clientId, clientSecret, options);
        }

        /// <summary>
        /// Creates a <see cref="TokenCredential"/> using a certificate. If no thumbprint is provided a <see cref="DefaultAzureCredential"/> is returned.
        /// </summary>
        /// <param name="thumbPrint">The thumbprint of the certificate to use for authentication</param>
        /// <param name="authority">A URL that indicates a directory to request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/. The tenant id is the first non-empty path segment and any deeper segments are ignored, so a /v2.0-suffixed issuer URL copied out of the Azure portal is accepted. The scheme+host becomes the credential's <see cref="Azure.Identity.TokenCredentialOptions.AuthorityHost"/>.</param>
        /// <param name="validCertsOnly">Indicates if only valid certificates can be found and used from the X509 cert store. If using self-signed certs, this value should be false.</param>
        /// <returns>A <see cref="TokenCredential"/>: a <see cref="ClientCertificateCredential"/> when a thumbprint is supplied, otherwise a <see cref="DefaultAzureCredential"/>.</returns>
        public TokenCredential WithCert(string thumbPrint, string authority, bool validCertsOnly, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            if (string.IsNullOrWhiteSpace(thumbPrint))
            {
                return BuildDefaultAzureCredential(optBuilder);
            }

            var cert = GetCertificate(thumbPrint, validCertsOnly);
            var (tenantId, authorityHost) = ParseAuthority(authority);
            var options = new ClientCertificateCredentialOptions();
            ApplyAuthorityHost(options, authorityHost);
            return new ClientCertificateCredential(tenantId, _clientId, cert, options);
        }

        /// <summary>
        /// Creates a <see cref="TokenCredential"/> using interactive login. If no redirect url is provided a <see cref="DefaultAzureCredential"/> is returned.
        /// </summary>
        /// <param name="redirectUri">The uri to redirect to after interactive login</param>
        /// <returns>A <see cref="TokenCredential"/>: an <see cref="InteractiveBrowserCredential"/> when a redirect uri is supplied, otherwise a <see cref="DefaultAzureCredential"/>.</returns>
        public TokenCredential WithInteractive(string redirectUri, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            if (string.IsNullOrWhiteSpace(redirectUri))
            {
                return BuildDefaultAzureCredential(optBuilder);
            }

            var options = new InteractiveBrowserCredentialOptions
            {
                ClientId = _clientId,
                RedirectUri = new Uri(redirectUri)
            };
            return new InteractiveBrowserCredential(options);
        }

        /// <summary>
        /// Creates a <see cref="TokenCredential"/> that authenticates as a managed identity, with no client secret, certificate, or authority required.
        /// </summary>
        /// <param name="optBuilder">Configures the <see cref="ManagedIdentityCredentialOptions"/> before the credential is constructed.</param>
        /// <returns>A <see cref="ManagedIdentityCredential"/> for the user-assigned managed identity named by the client id supplied to <see cref="Create(string)"/>, or for the system-assigned managed identity when that client id is null or whitespace.</returns>
        /// <remarks>No credential chain is constructed, so no ambient credential source can answer in place of a requested user-assigned identity. One bounded exception applies to the blank/system-assigned case only: on a federated-token host (for example AKS workload identity) the SDK's token-exchange managed-identity source falls back to the AZURE_CLIENT_ID environment variable when no identity was supplied, so that case authenticates as the platform-bound workload identity rather than literally system-assigned. Supplying a client id closes that fallback.</remarks>
        public TokenCredential WithManagedIdentity(Action<ManagedIdentityCredentialOptions> optBuilder = null)
        {
            var options = new ManagedIdentityCredentialOptions(ResolveManagedIdentityId(_clientId));
            optBuilder?.Invoke(options);
            return new ManagedIdentityCredential(options);
        }

        // INVARIANT: the requested identity is carried by the credential itself, never selected from
        // a credential chain. A chain picks its arm from ambient host state, so an environment-backed
        // service principal can answer a managed identity request; constructing the credential
        // directly from the caller's client id makes that substitution unrepresentable.
        internal static ManagedIdentityId ResolveManagedIdentityId(string clientId)
            => string.IsNullOrWhiteSpace(clientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId);

        private static DefaultAzureCredential BuildDefaultAzureCredential(Action<DefaultAzureCredentialOptions> optBuilder)
        {
            var opts = new DefaultAzureCredentialOptions();
            optBuilder?.Invoke(opts);
            return new DefaultAzureCredential(opts);
        }

        // INVARIANT: the legacy MSAL authority was a full directory URL (scheme+host+tenant path),
        // e.g. https://login.microsoftonline.com/{tenant}/. Azure.Identity credentials take the
        // tenant id separately and the scheme+host as AuthorityHost, so the URL is split here: the
        // tenant id is the FIRST NON-EMPTY path segment; every deeper segment is authority-endpoint
        // routing (v2.0, oauth2/v2.0/token) and is ignored. First non-empty rather than Segments[1]
        // because a double-slash authority (host followed by //{tenant}/) carries an empty leading
        // segment, which Segments[1] would resolve to as the tenant id.
        // A blank, non-absolute, or path-less authority yields a null tenant id. Every caller hands
        // that straight to an Azure.Identity credential constructor, which rejects null, so those
        // shapes throw ArgumentNullException at construction — nothing is defaulted. A null
        // authorityHost only ever accompanies a null tenant id, so the null arm of ApplyAuthorityHost
        // is unobservable from WithSecret and WithCert.
        // An authority whose first segment is not the tenant id resolves a wrong tenant id and still
        // constructs; that is an operator configuration error, not a case handled here, and CONTEXT.md
        // "Directory Authority" carries the accepted-residue disposition.
        // SCOPE RULE: this block states construction-time facts about THIS code only, and each such fact
        // is pinned by a WhenSelectingCredentialBranches assertion whose failure would falsify it. What
        // Entra does with the constructed credential is outside this code's authority — no claim about
        // token-acquisition outcomes may be added to this block, pinned or not.
        private static (string tenantId, Uri authorityHost) ParseAuthority(string authority)
        {
            if (string.IsNullOrWhiteSpace(authority)
                || !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
            {
                return (null, null);
            }

            var tenantId = authorityUri.Segments
                .Select(segment => segment.Trim('/'))
                .FirstOrDefault(segment => !string.IsNullOrWhiteSpace(segment));
            var authorityHost = new Uri(authorityUri.GetLeftPart(UriPartial.Authority));
            return (string.IsNullOrWhiteSpace(tenantId) ? null : tenantId, authorityHost);
        }

        private static void ApplyAuthorityHost(TokenCredentialOptions options, Uri authorityHost)
        {
            if (authorityHost != null)
            {
                options.AuthorityHost = authorityHost;
            }
        }

        X509Certificate2 GetCertificate(string thumbPrint, bool validCertsOnly)
        {
            List<StoreLocation> locations = new List<StoreLocation>
            {
                StoreLocation.CurrentUser,
                StoreLocation.LocalMachine
            };

            foreach (var location in locations)
            {
                X509Store store = new X509Store(StoreName.My, location);
                try
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    X509Certificate2Collection certificates = store.Certificates.Find(
                        X509FindType.FindByThumbprint, thumbPrint, validCertsOnly);
                    if (certificates.Count >= 1)
                    {
                        return certificates[0];
                    }
                }
                finally
                {
                    store.Close();
                }
            }

            throw new ArgumentException($"A Certificate with Thumbprint '{thumbPrint}' could not be located.");
        }
    }
}
