using Chatter.MessageBrokers.Options;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers.Saga.Configuration
{
    public class SagaOptions
    {
        [Required(AllowEmptyStrings = false, ErrorMessage = "The saga data type used for this saga is required.")]
        public string SagaDataType { get; set; }
        public int MaxSagaDurationInMinutes { get; set; } = SagaConfigurationConstants.DefaultMaxSagaDurationInMinutes;
        public string SagaDataContentType { get; set; } = SagaConfigurationConstants.DefaultSagaDataContentType;
        public string Description { get; set; } = SagaConfigurationConstants.DefaultDescription;
        [JsonIgnore]
        public TransactionMode TransactionMode { get; internal set; } = TransactionMode.None;
        public string DefaultTransactionMode { get; set; }
    }

    public class SagaConfigurationConstants
    {
        public const string DefaultSagaDataContentType = "application/json";
        public const int DefaultMaxSagaDurationInMinutes = 999;
        public const string DefaultDescription = "Saga";
    }
}
