using Azure.Identity;
using Chatter.MessageBrokers.AzureServiceBus.Auth;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ServiceBusOptionsBuilderExtensions
    {
        /// <summary>
        /// Uses a <see cref="Azure.Core.TokenCredential"/> for Azure Service Bus authentication via a client secret. If no client secret is provided a <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="builder">The <see cref="ServiceBusOptionsBuilder"/> used to configure Azure Service Bus authentication</param>
        /// <param name="clientId">The client ID of the service principal</param>
        /// <param name="clientSecret">The client secret to use to authenticate with Azure AD</param>
        /// <param name="authority">A URL that indicates a directory to request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/. The tenant id is the first non-empty path segment and any deeper segments are ignored, so a /v2.0-suffixed issuer URL copied out of the Azure portal is accepted. The scheme+host becomes the credential's <see cref="Azure.Identity.TokenCredentialOptions.AuthorityHost"/>.</param>
        /// <param name="optBuilder">An optional builder to construct <see cref="DefaultAzureCredentialOptions"/> to be used with <see cref="DefaultAzureCredential"/> when no client secret is provided.</param>
        /// <returns>a <see cref="ServiceBusOptionsBuilder"/></returns>
        public static ServiceBusOptionsBuilder UseAadTokenProviderWithSecret(this ServiceBusOptionsBuilder builder, string clientId, string clientSecret, string authority, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithSecret(clientSecret, authority, optBuilder));
            return builder;
        }

        /// <summary>
        /// Uses a <see cref="Azure.Core.TokenCredential"/> for Azure Service Bus authentication via a client certificate. If no thumbprint is provided a <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="builder">The <see cref="ServiceBusOptionsBuilder"/> used to configure Azure Service Bus authentication</param>
        /// <param name="clientId">The client ID of the service principal</param>
        /// <param name="thumbPrint">The thumbprint of the certificate to be used for authentication</param>
        /// <param name="authority">A URL that indicates a directory to request tokens from. For example, https://login.microsoftonline.com/{AzureADTenantID}/. The tenant id is the first non-empty path segment and any deeper segments are ignored, so a /v2.0-suffixed issuer URL copied out of the Azure portal is accepted. The scheme+host becomes the credential's <see cref="Azure.Identity.TokenCredentialOptions.AuthorityHost"/>.</param>
        /// <param name="optBuilder">An optional builder to construct <see cref="DefaultAzureCredentialOptions"/> to be used with <see cref="DefaultAzureCredential"/> when no thumbprint is provided.</param>
        /// <param name="validCertsOnly"></param>
        /// <returns>a <see cref="ServiceBusOptionsBuilder"/></returns>
        public static ServiceBusOptionsBuilder UseAadTokenProviderWithCert(this ServiceBusOptionsBuilder builder, string clientId, string thumbPrint, string authority, Action<DefaultAzureCredentialOptions> optBuilder = null, bool validCertsOnly = true)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithCert(thumbPrint, authority, validCertsOnly, optBuilder));
            return builder;
        }

        /// <summary>
        /// Uses a <see cref="Azure.Core.TokenCredential"/> for Azure Service Bus authentication via interactive login. If no redirect url is provided a <see cref="DefaultAzureCredential"/> is used.
        /// </summary>
        /// <param name="builder">The <see cref="ServiceBusOptionsBuilder"/> used to configure Azure Service Bus authentication</param>
        /// <param name="clientId">The client ID of the service principal</param>
        /// <param name="redirectUri">The uri to redirect to after successful interactive login</param>
        /// <param name="optBuilder">A builder to construct <see cref="DefaultAzureCredentialOptions"/> to be used with <see cref="DefaultAzureCredential"/> when no redirect url is provided.</param>
        /// <returns>a <see cref="ServiceBusOptionsBuilder"/></returns>
        public static ServiceBusOptionsBuilder UseAadTokenProviderInteractively(this ServiceBusOptionsBuilder builder, string clientId, string redirectUri, Action<DefaultAzureCredentialOptions> optBuilder = null)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithInteractive(redirectUri, optBuilder));
            return builder;
        }

        /// <summary>
        /// Uses a <see cref="Azure.Core.TokenCredential"/> for Azure Service Bus authentication as an Azure managed identity, so no client secret, certificate, or authority is required. A <see cref="ManagedIdentityCredential"/> is constructed directly: this mode never consults the <see cref="DefaultAzureCredential"/> chain, so no other credential source can answer in place of a requested user-assigned identity and the AZURE_TOKEN_CREDENTIALS environment variable does not affect it. See <paramref name="clientId"/> for the one bounded exception on the blank/system-assigned case.
        /// </summary>
        /// <param name="builder">The <see cref="ServiceBusOptionsBuilder"/> used to configure Azure Service Bus authentication</param>
        /// <param name="clientId">The client ID of the user-assigned managed identity. Omit it, or supply null or whitespace, to authenticate as the system-assigned managed identity. Bounded exception: on a federated-token host (for example AKS workload identity) the SDK's token-exchange managed-identity source falls back to the AZURE_CLIENT_ID environment variable when no identity was supplied, so the blank case authenticates as the platform-bound workload identity rather than literally system-assigned. Supplying a client ID closes that fallback.</param>
        /// <param name="optBuilder">An optional builder to construct <see cref="ManagedIdentityCredentialOptions"/>, invoked before the credential is constructed. To supply only an opt builder, use the named-argument form <c>UseAadTokenProviderWithManagedIdentity(optBuilder: opts =&gt; ...)</c>: a bare lambda in the first positional slot is a compile error because that slot is <paramref name="clientId"/>.</param>
        /// <returns>a <see cref="ServiceBusOptionsBuilder"/></returns>
        public static ServiceBusOptionsBuilder UseAadTokenProviderWithManagedIdentity(this ServiceBusOptionsBuilder builder, string clientId = null, Action<ManagedIdentityCredentialOptions> optBuilder = null)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithManagedIdentity(optBuilder));
            return builder;
        }
    }
}
