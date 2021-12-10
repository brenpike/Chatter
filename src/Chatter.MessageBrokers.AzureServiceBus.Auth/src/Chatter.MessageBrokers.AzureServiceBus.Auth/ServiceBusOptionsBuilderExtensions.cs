using Azure.Identity;
using Chatter.MessageBrokers.AzureServiceBus.Auth;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using System;
using Microsoft.Azure.ServiceBus.Primitives;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ServiceBusOptionsBuilderExtensions
    {
        /// <summary>
        /// Uses an <see cref="AzureActiveDirectoryTokenProvider"/> for Azure Service Bus authentication via a client secret. If no client secret is provided <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="builder">The <see cref="ServiceBusOptionsBuilder"/> used to configure Azure Service Bus authentication</param>
        /// <param name="clientId">The client ID of the service principal</param>
        /// <param name="clientSecret">The client secret to use to authenticate with Azure AD</param>
        /// <param name="authority">A URL that indicates a directory that MSAL can request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/</param>
        /// <param name="optBuilder">An optional builder to construct <see cref="DefaultAzureCredentialOptions"/> to be used with <see cref="DefaultAzureCredential"/> when no client secret is provided.</param>
        /// <returns>a <see cref="ServiceBusOptionsBuilder"/></returns>
        public static ServiceBusOptionsBuilder UseAadTokenProviderWithSecret(this ServiceBusOptionsBuilder builder, string clientId, string clientSecret, string authority, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithSecret(clientSecret, authority));
            return builder;
        }

        /// <summary>
        /// Uses an <see cref="AzureActiveDirectoryTokenProvider"/> for Azure Service Bus authentication via a client certificate. If no thumbprint is provided <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="builder">The <see cref="ServiceBusOptionsBuilder"/> used to configure Azure Service Bus authentication</param>
        /// <param name="clientId">The client ID of the service principal</param>
        /// <param name="thumbPrint">The thumbprint of the certificate to be used for authentication</param>
        /// <param name="authority">A URL that indicates a directory that MSAL can request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/</param>
        /// <param name="optBuilder">An optional builder to construct <see cref="DefaultAzureCredentialOptions"/> to be used with <see cref="DefaultAzureCredential"/> when no thumbprint is provided.</param>
        /// <param name="validCertsOnly"></param>
        /// <returns>a <see cref="ServiceBusOptionsBuilder"/></returns>
        public static ServiceBusOptionsBuilder UseAadTokenProviderWithCert(this ServiceBusOptionsBuilder builder, string clientId, string thumbPrint, string authority, Action<DefaultAzureCredentialOptions> optBuilder = null, bool validCertsOnly = true)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithCert(thumbPrint, authority, validCertsOnly));
            return builder;
        }

        /// <summary>
        /// Uses an <see cref="AzureActiveDirectoryTokenProvider"/> for Azure Service Bus authentication via a client certificate. If no redirect url is provided <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="builder">The <see cref="ServiceBusOptionsBuilder"/> used to configure Azure Service Bus authentication</param>
        /// <param name="clientId">The client ID of the service principal</param>
        /// <param name="redirectUri">The uri to redirect to after successful interactive login</param>
        /// <param name="optBuilder">A builder to construct <see cref="DefaultAzureCredentialOptions"/> to be used with <see cref="DefaultAzureCredential"/> when no redirect url is provided.</param>
        /// <returns>a <see cref="ServiceBusOptionsBuilder"/></returns>
        public static ServiceBusOptionsBuilder UseAadTokenProviderInteractively(this ServiceBusOptionsBuilder builder, string clientId, string redirectUri, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithInteractive(redirectUri));
            return builder;
        }
    }
}
