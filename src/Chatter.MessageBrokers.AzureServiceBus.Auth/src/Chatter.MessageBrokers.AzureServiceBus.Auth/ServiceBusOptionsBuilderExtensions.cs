using Chatter.MessageBrokers.AzureServiceBus.Auth;
using Chatter.MessageBrokers.AzureServiceBus.Options;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ServiceBusOptionsBuilderExtensions
    {
        public static ServiceBusOptionsBuilder UseAadTokenProviderWithSecret(this ServiceBusOptionsBuilder builder, string clientId, string clientSecret, string authority, object state = null)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithSecret(clientSecret, authority, state));
            return builder;
        }

        public static ServiceBusOptionsBuilder UseAadTokenProviderWithCert(this ServiceBusOptionsBuilder builder, string clientId, string thumbPrint, string authority, object state = null)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithCert(thumbPrint, authority, state));
            return builder;
        }

        public static ServiceBusOptionsBuilder UseAadTokenProviderInteractively(this ServiceBusOptionsBuilder builder, string clientId, string redirectUri, object state = null)
        {
            builder.AddTokenProvider(() => AadTokenProviderFactory.Create(clientId).WithInteractive(redirectUri, state));
            return builder;
        }
    }
}
