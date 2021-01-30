using Microsoft.Azure.ServiceBus.Primitives;
using Microsoft.Identity.Client;
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

namespace Chatter.MessageBrokers.AzureServiceBus.Auth
{
    public class AadTokenProviderFactory
    {
        private const string AadServiceBusAudience = "https://servicebus.azure.net/";
        private readonly string _clientId;
        private readonly Uri _serviceBusAudience;

        public static AadTokenProviderFactory Create(string clientId) => new AadTokenProviderFactory(clientId);

        private AadTokenProviderFactory(string clientId)
        {
            _clientId = clientId;
            _serviceBusAudience = new Uri(AadServiceBusAudience);
        }

        /// <summary>
        /// Creates an <see cref="AzureActiveDirectoryTokenProvider"/> using a client secret
        /// </summary>
        /// <param name="clientSecret">The client secret to use to authenticate with Azure AD</param>
        /// <param name="authority">A URL that indicates a directory that MSAL can request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/</param>
        /// <param name="state">Custom state to provide the auth callback</param>
        /// <returns><see cref="AzureActiveDirectoryTokenProvider"/></returns>
        public AzureActiveDirectoryTokenProvider WithSecret(string clientSecret, string authority, object state = null)
        {
            AzureActiveDirectoryTokenProvider.AuthenticationCallback authCallback = async (audience, authority, state) =>
            {
                IConfidentialClientApplication app = ConfidentialClientApplicationBuilder.Create(_clientId)
                                .WithAuthority(authority)
                                .WithClientSecret(clientSecret)
                                .Build();

                var authResult = await app.AcquireTokenForClient(new string[] { $"{_serviceBusAudience}/.default" }).ExecuteAsync();

                return authResult.AccessToken;
            };

            return new AzureActiveDirectoryTokenProvider(authCallback, authority, state);
        }

        /// <summary>
        /// Creates an <see cref="AzureActiveDirectoryTokenProvider"/> using a certificate
        /// </summary>
        /// <param name="thumbPrint">The thumbprint of the certificate to use for authentication</param>
        /// <param name="authority">A URL that indicates a directory that MSAL can request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/
        /// <param name="validCertsOnly">Indicates if only valid certificates are can be found and used from the X509 cert store. If using self-signed certs, this value should be false.</param>
        /// <param name="state">Custom state to provide the auth callback</param>
        /// <returns><see cref="AzureActiveDirectoryTokenProvider"/></returns>
        public AzureActiveDirectoryTokenProvider WithCert(string thumbPrint, string authority, bool validCertsOnly, object state = null)
        {
            var cert = GetCertificate(thumbPrint, validCertsOnly);
            AzureActiveDirectoryTokenProvider.AuthenticationCallback authCallback = async (audience, authority, state) =>
            {
                IConfidentialClientApplication app = ConfidentialClientApplicationBuilder.Create(_clientId)
                                .WithAuthority(authority)
                                .WithCertificate(cert)
                                .Build();

                var authResult = await app.AcquireTokenForClient(new string[] { $"{_serviceBusAudience}/.default" }).ExecuteAsync();
                return authResult.AccessToken;
            };

            return new AzureActiveDirectoryTokenProvider(authCallback, authority, state);
        }

        /// <summary>
        /// Creates an <see cref="AzureActiveDirectoryTokenProvider"/> using interactive login
        /// </summary>
        /// <param name="redirectUri">The uri to redirect to after interactive login</param>
        /// <param name="state">Custom state to provide the auth callback</param>
        /// <returns><see cref="AzureActiveDirectoryTokenProvider"/></returns>
        public AzureActiveDirectoryTokenProvider WithInteractive(string redirectUri, object state = null)
        {
            AzureActiveDirectoryTokenProvider.AuthenticationCallback authCallback = async (audience, authority, state) =>
            {
                IConfidentialClientApplication app = ConfidentialClientApplicationBuilder.Create(_clientId)
                                .WithRedirectUri(redirectUri)
                                .Build();

                var authResult = await app.AcquireTokenForClient(new string[] { $"{_serviceBusAudience}/.default" }).ExecuteAsync();
                return authResult.AccessToken;
            };

            return new AzureActiveDirectoryTokenProvider(authCallback, "", state);
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
