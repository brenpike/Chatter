using Azure.Core;
using Azure.Identity;
using System;
using System.Collections.Generic;
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
        /// <param name="authority">A URL that indicates a directory to request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/. The tenant id is parsed from the path and the scheme+host becomes the credential's <see cref="Azure.Identity.TokenCredentialOptions.AuthorityHost"/>.</param>
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
        /// <param name="authority">A URL that indicates a directory to request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/. The tenant id is parsed from the path and the scheme+host becomes the credential's <see cref="Azure.Identity.TokenCredentialOptions.AuthorityHost"/>.</param>
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
        /// <param name="optBuilder">Configures the <see cref="DefaultAzureCredentialOptions"/> after the client id supplied to <see cref="Create(string)"/> has been applied, so an explicit assignment here wins.</param>
        /// <returns>A <see cref="DefaultAzureCredential"/> whose managed identity and workload identity client ids are the client id supplied to <see cref="Create(string)"/>.</returns>
        public TokenCredential WithManagedIdentity(Action<DefaultAzureCredentialOptions> optBuilder = null)
            => BuildDefaultAzureCredential(_clientId, optBuilder);

        private static DefaultAzureCredential BuildDefaultAzureCredential(Action<DefaultAzureCredentialOptions> optBuilder)
            => BuildDefaultAzureCredential(null, optBuilder);

        // INVARIANT: ManagedIdentityClientId and WorkloadIdentityClientId both default to the
        // AZURE_CLIENT_ID environment variable, so a blank identityClientId must leave them untouched
        // rather than overwrite an ambient identity with an empty pin. The pin is applied before
        // optBuilder runs so an explicit assignment by the caller still wins.
        private static DefaultAzureCredential BuildDefaultAzureCredential(string identityClientId, Action<DefaultAzureCredentialOptions> optBuilder)
        {
            var opts = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(identityClientId))
            {
                opts.ManagedIdentityClientId = identityClientId;
                opts.WorkloadIdentityClientId = identityClientId;
            }

            optBuilder?.Invoke(opts);
            return new DefaultAzureCredential(opts);
        }

        // INVARIANT: the legacy MSAL authority was a full directory URL (scheme+host+tenant path),
        // e.g. https://login.microsoftonline.com/{tenant}/. Azure.Identity credentials take the
        // tenant id separately and the scheme+host as AuthorityHost, so the URL is split here:
        // the first path segment is the tenant id; the scheme+host becomes AuthorityHost. A null,
        // empty, or unparseable authority yields a null tenant id and null AuthorityHost, deferring
        // to the credential's own defaults (mirrors the old (authority ?? "") coercion).
        private static (string tenantId, Uri authorityHost) ParseAuthority(string authority)
        {
            if (string.IsNullOrWhiteSpace(authority)
                || !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
            {
                return (null, null);
            }

            var tenantId = authorityUri.AbsolutePath.Trim('/');
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
