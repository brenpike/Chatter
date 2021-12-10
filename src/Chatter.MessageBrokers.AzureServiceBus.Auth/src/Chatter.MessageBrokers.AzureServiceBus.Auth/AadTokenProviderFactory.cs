using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.ServiceBus.Primitives;
using Microsoft.Identity.Client;
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth
{
    public class AadTokenProviderFactory
    {
        private readonly string _clientId;
        private readonly TokenRequestContext _requestContext;

        public static AadTokenProviderFactory Create(string clientId) => new AadTokenProviderFactory(clientId);

        private AadTokenProviderFactory(string clientId)
        {
            _clientId = clientId;
            _requestContext = new TokenRequestContext(new[] { "https://servicebus.azure.net/.default" });
        }

        /// <summary>
        /// Creates an <see cref="AzureActiveDirectoryTokenProvider"/> using a client secret. If no client secret is provided <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="clientSecret">The client secret to use to authenticate with Azure AD</param>
        /// <param name="authority">A URL that indicates a directory that MSAL can request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/</param>
        /// <returns><see cref="AzureActiveDirectoryTokenProvider"/></returns>
        public AzureActiveDirectoryTokenProvider WithSecret(string clientSecret, string authority, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            AzureActiveDirectoryTokenProvider.AuthenticationCallback authCallback = async (audience, authority, state) =>
            {
                if (string.IsNullOrWhiteSpace(clientSecret))
                {
                    return (await GetTokenFromDefaultAzureCredential(optBuilder)).Token;
                }

                IConfidentialClientApplication app = ConfidentialClientApplicationBuilder.Create(_clientId)
                                .WithAuthority(authority)
                                .WithClientSecret(clientSecret)
                                .Build();

                var authResult = await app.AcquireTokenForClient(_requestContext.Scopes).ExecuteAsync();

                return authResult.AccessToken;
            };

            return new AzureActiveDirectoryTokenProvider(authCallback, authority ?? "", null);
        }

        /// <summary>
        /// Creates an <see cref="AzureActiveDirectoryTokenProvider"/> using a certificate. If no thumbprint is provided <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="thumbPrint">The thumbprint of the certificate to use for authentication</param>
        /// <param name="authority">A URL that indicates a directory that MSAL can request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/
        /// <param name="validCertsOnly">Indicates if only valid certificates are can be found and used from the X509 cert store. If using self-signed certs, this value should be false.</param>
        /// <returns><see cref="AzureActiveDirectoryTokenProvider"/></returns>
        public AzureActiveDirectoryTokenProvider WithCert(string thumbPrint, string authority, bool validCertsOnly, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            AzureActiveDirectoryTokenProvider.AuthenticationCallback authCallback = async (audience, authority, state) =>
            {
                if (string.IsNullOrWhiteSpace(thumbPrint))
                {
                    return (await GetTokenFromDefaultAzureCredential(optBuilder)).Token;
                }

                var cert = GetCertificate(thumbPrint, validCertsOnly);

                IConfidentialClientApplication app = ConfidentialClientApplicationBuilder.Create(_clientId)
                                .WithAuthority(authority)
                                .WithCertificate(cert)
                                .Build();

                var authResult = await app.AcquireTokenForClient(_requestContext.Scopes).ExecuteAsync();
                return authResult.AccessToken;
            };

            return new AzureActiveDirectoryTokenProvider(authCallback, authority ?? "", null);
        }

        /// <summary>
        /// Creates an <see cref="AzureActiveDirectoryTokenProvider"/> using interactive login. If no redirect url is provided <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="redirectUri">The uri to redirect to after interactive login</param>
        /// <returns><see cref="AzureActiveDirectoryTokenProvider"/></returns>
        public AzureActiveDirectoryTokenProvider WithInteractive(string redirectUri, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            AzureActiveDirectoryTokenProvider.AuthenticationCallback authCallback = async (audience, authority, state) =>
            {
                if (string.IsNullOrWhiteSpace(redirectUri))
                {
                    return (await GetTokenFromDefaultAzureCredential(optBuilder)).Token;
                }

                IConfidentialClientApplication app = ConfidentialClientApplicationBuilder.Create(_clientId)
                                .WithRedirectUri(redirectUri)
                                .Build();

                var authResult = await app.AcquireTokenForClient(_requestContext.Scopes).ExecuteAsync();
                return authResult.AccessToken;
            };

            return new AzureActiveDirectoryTokenProvider(authCallback, "", null);
        }

        private ValueTask<AccessToken> GetTokenFromDefaultAzureCredential(Action<DefaultAzureCredentialOptions> optBuilder)
        {
            var opts = new DefaultAzureCredentialOptions();
            optBuilder?.Invoke(opts);
            var defaultCredentials = new DefaultAzureCredential(opts);
            return defaultCredentials.GetTokenAsync(_requestContext, System.Threading.CancellationToken.None);
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
