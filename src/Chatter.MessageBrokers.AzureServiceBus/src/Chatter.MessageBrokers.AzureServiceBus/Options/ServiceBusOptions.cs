using Azure.Core;
using Azure.Messaging.ServiceBus;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    /// <summary>
    /// A class containing various Azure Service Bus configuration values.
    /// </summary>
    public class ServiceBusOptions
    {
        [Required(AllowEmptyStrings = false, ErrorMessage = "A service bus connection string is required.")]
        public string ConnectionString { get; set; }
        public int MaxConcurrentCalls { get; set; } = 1;
        public int PrefetchCount { get; set; } = 0;
        internal RetryPolicyConfiguation RetryPolicy { get; set; }
        [JsonIgnore]
        public ServiceBusRetryOptions RetryOptions { get; internal set; }
        // INVARIANT: a null TokenCredential means "authenticate using the connection string's SAS".
        [JsonIgnore]
        public TokenCredential TokenCredential { get; internal set; } = null;
    }

    internal class RetryPolicyConfiguation
    {
        public double MinimumBackoffInSeconds { get; set; } = 0;
        public double MaximumBackoffInSeconds { get; set; } = 0;
        public int MaximumRetryCount { get; set; } = 0;
        public double DeltaBackoffInSeconds { get; set; } = 0;
    }
}
