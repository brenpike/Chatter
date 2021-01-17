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

        public AzureActiveDirectoryTokenProvider WithCert(string thumbPrint, string authority, object state = null)
        {
            var cert = GetCertificate(thumbPrint);
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

        X509Certificate2 GetCertificate(string thumbPrint)
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
                        X509FindType.FindByThumbprint, thumbPrint, true);
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
