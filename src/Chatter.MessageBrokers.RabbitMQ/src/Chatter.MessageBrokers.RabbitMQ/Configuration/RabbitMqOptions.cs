namespace Chatter.MessageBrokers.RabbitMQ.Configuration
{
    /// <summary>
    /// The durable queue type a receiver's queue is declared as.
    /// </summary>
    public enum QueueType
    {
        Quorum,
        Classic
    }

    public sealed class RabbitMqOptions
    {
        /// <summary>
        /// The AMQP connection URI (e.g. amqp://user:pass@host:5672/vhost). When set, takes precedence over the discrete host/credential settings.
        /// </summary>
        public string Uri { get; set; }
        /// <summary>
        /// The RabbitMQ broker host name.
        /// </summary>
        public string HostName { get; set; }
        /// <summary>
        /// The user name used to authenticate with the broker.
        /// </summary>
        public string UserName { get; set; }
        /// <summary>
        /// The password used to authenticate with the broker.
        /// </summary>
        public string Password { get; set; }
        /// <summary>
        /// The content type of the message body. The default is application/json; charset=utf-8.
        /// </summary>
        public string MessageBodyType { get; set; } = "application/json; charset=utf-8";
        /// <summary>
        /// The maximum number of unacknowledged messages the broker will deliver to a receiver before waiting for acknowledgements.
        /// </summary>
        public int Prefetch { get; set; } = 1;
        /// <summary>
        /// The queue type receivers declare their queues as. Defaults to <see cref="QueueType.Quorum"/>.
        /// </summary>
        public QueueType QueueType { get; set; } = QueueType.Quorum;

        public RabbitMqOptions(string uri = null,
                               string hostName = null,
                               string userName = null,
                               string password = null,
                               string messageBodyType = "application/json; charset=utf-8",
                               int prefetch = 1,
                               QueueType queueType = QueueType.Quorum)
        {
            Uri = uri;
            HostName = hostName;
            UserName = userName;
            Password = password;
            MessageBodyType = messageBodyType;
            Prefetch = prefetch;
            QueueType = queueType;
        }
    }
}
