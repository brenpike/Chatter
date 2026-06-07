using Chatter.MessageBrokers.AzureServiceBus.Options;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Azure.ServiceBus.Primitives;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    /// <summary>
    /// Production <see cref="IServiceBusMessageSenderFactory"/> that reproduces the VERBATIM three-branch
    /// <see cref="MessageSender"/> construction previously inlined in <see cref="BrokeredMessageSenderPool.GetOrCreate"/>:
    /// send-via an existing receiver connection, namespace connection string under a
    /// <see cref="NullTokenProvider"/>, or endpoint + token provider. The constructed sender opens a
    /// live connection, so this branch selection is exercised in production only.
    /// </summary>
    internal class AzureSdkMessageSenderFactory : IServiceBusMessageSenderFactory
    {
        readonly ServiceBusConnectionStringBuilder _connectionStringBuilder;
        readonly RetryPolicy _retryPolicy;
        private readonly ITokenProvider _tokenProvider;

        public AzureSdkMessageSenderFactory(ServiceBusOptions serviceBusOptions)
        {
            if (serviceBusOptions == null)
            {
                throw new ArgumentNullException(nameof(serviceBusOptions), $"Service Bus options are required to use {nameof(AzureSdkMessageSenderFactory)}");
            }

            _retryPolicy = serviceBusOptions.Policy;
            _connectionStringBuilder = new ServiceBusConnectionStringBuilder(serviceBusOptions.ConnectionString);
            _tokenProvider = serviceBusOptions.TokenProvider;
        }

        public MessageSender Create(string destinationEntityPath, (ServiceBusConnection connection, string sendViaPath) receiverConnectionAndPath)
        {
            if (receiverConnectionAndPath.connection != null && receiverConnectionAndPath.sendViaPath != null)
            {
                return new MessageSender(receiverConnectionAndPath.connection, destinationEntityPath, receiverConnectionAndPath.sendViaPath, _retryPolicy);
            }

            if (_tokenProvider is NullTokenProvider)
            {
                return new MessageSender(_connectionStringBuilder.GetNamespaceConnectionString(), destinationEntityPath, _retryPolicy);
            }

            return new MessageSender(_connectionStringBuilder.Endpoint, destinationEntityPath, _tokenProvider, _connectionStringBuilder.TransportType, _retryPolicy);
        }
    }
}
