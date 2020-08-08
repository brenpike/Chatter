using Microsoft.Azure.ServiceBus;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    /// <summary>
    /// A class containing various Azure Service Bus configuration values.
    /// </summary>
    public class ServiceBusOptions
    {
        [Required(AllowEmptyStrings = false, ErrorMessage = "A service bus connection string is required.")]
        public string ConnectionString { get; set; }
        public TransportType TransportType { get; set; } = TransportType.AmqpWebSockets;
        public ReceiveMode ReceiveMode { get; set; } = ReceiveMode.PeekLock;
        public RetryPolicyConfiguation RetryPolicy { get; set; }

        [JsonIgnore]
        public RetryPolicy Policy { get; internal set; }
    }

    public class RetryPolicyConfiguation
    {
        public int MinimumBackoff { get; set; } = 0;
        public int MaximumBackoff { get; set; } = 0;
        public int MaximumRetryCount { get; set; } = 0;
    }
}
