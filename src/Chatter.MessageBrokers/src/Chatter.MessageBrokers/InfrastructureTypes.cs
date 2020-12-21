namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Infrastructure Types are implemented through extension methods
    /// </summary>
    public class InfrastructureTypes
    {
        /// <summary>
        /// Choosing the <see cref="Default"/> infrastructure type will result in the first infrastructure registered in the container
        /// </summary>
        public string Default => "";
    }
}
