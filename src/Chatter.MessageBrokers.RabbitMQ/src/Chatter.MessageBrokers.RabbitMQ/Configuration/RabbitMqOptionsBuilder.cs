using Chatter.CQRS;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.RabbitMQ.Configuration
{
    public class RabbitMqOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private RabbitMqOptions _rabbitMqOptions;
        private const string _defaultMessageBodyType = "application/json; charset=utf-8";

        public RabbitMqOptionsBuilder(IServiceCollection services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public RabbitMqOptionsBuilder AddRabbitMqOptions(RabbitMqOptions options)
        {
            _rabbitMqOptions = options;
            return this;
        }

        public RabbitMqOptionsBuilder AddRabbitMqOptions(Func<RabbitMqOptions> optionsBuilder)
        {
            _rabbitMqOptions = optionsBuilder();
            return this;
        }

        public RabbitMqOptionsBuilder AddRabbitMqOptions(string uri = null,
                                                         string hostName = null,
                                                         string userName = null,
                                                         string password = null,
                                                         string messageBodyType = _defaultMessageBodyType,
                                                         int prefetch = 1,
                                                         QueueType queueType = QueueType.Quorum)
        {
            _rabbitMqOptions = new RabbitMqOptions(uri,
                                                   hostName,
                                                   userName,
                                                   password,
                                                   messageBodyType,
                                                   prefetch,
                                                   queueType);
            return this;
        }

        /// <summary>
        /// Sets the AMQP connection URI used for all RabbitMQ communication.
        /// </summary>
        /// <param name="uri">The AMQP connection URI.</param>
        public RabbitMqOptionsBuilder WithUri(string uri)
        {
            _rabbitMqOptions.Uri = uri;
            return this;
        }

        /// <summary>
        /// Sets the RabbitMQ broker host name.
        /// </summary>
        /// <param name="hostName">The broker host name.</param>
        public RabbitMqOptionsBuilder WithHostName(string hostName)
        {
            _rabbitMqOptions.HostName = hostName;
            return this;
        }

        /// <summary>
        /// Sets the credentials used to authenticate with the broker.
        /// </summary>
        /// <param name="userName">The user name.</param>
        /// <param name="password">The password.</param>
        public RabbitMqOptionsBuilder WithCredentials(string userName, string password)
        {
            _rabbitMqOptions.UserName = userName;
            _rabbitMqOptions.Password = password;
            return this;
        }

        /// <summary>
        /// Sets the content type of the RabbitMQ message body. The content type is used to encode
        /// to/from <see cref="string"/> and <see cref="byte[]"/>.
        /// </summary>
        /// <param name="messageBodyType">The message body type used to encode the RabbitMQ message body.</param>
        public RabbitMqOptionsBuilder WithMessageBodyType(string messageBodyType)
        {
            _rabbitMqOptions.MessageBodyType = messageBodyType;
            return this;
        }

        /// <summary>
        /// Sets the content type of the RabbitMQ message body to application/json; charset=utf-8.
        /// </summary>
        public RabbitMqOptionsBuilder WithJsonBodyType()
        {
            _rabbitMqOptions.MessageBodyType = _defaultMessageBodyType;
            return this;
        }

        /// <summary>
        /// Sets the maximum number of unacknowledged messages the broker will deliver to a receiver
        /// before waiting for acknowledgements.
        /// </summary>
        /// <param name="prefetch">The prefetch count.</param>
        public RabbitMqOptionsBuilder WithPrefetch(int prefetch)
        {
            _rabbitMqOptions.Prefetch = prefetch;
            return this;
        }

        /// <summary>
        /// Sets the queue type receivers declare their queues as.
        /// </summary>
        /// <param name="queueType">The queue type.</param>
        public RabbitMqOptionsBuilder WithQueueType(QueueType queueType)
        {
            _rabbitMqOptions.QueueType = queueType;
            return this;
        }

        /// <summary>
        /// Registers a receiver for <typeparamref name="TMessage"/> bound to the supplied queue.
        /// </summary>
        public RabbitMqOptionsBuilder AddQueueReceiver<TMessage>(string queueName,
                                                                 string errorQueuePath = null,
                                                                 string description = null,
                                                                 TransactionMode? transactionMode = null,
                                                                 string deadLetterQueuePath = null,
                                                                 int maxReceiveAttempts = 10)
            where TMessage : class, IMessage
        {
            Services.AddReceiver<TMessage>(queueName, errorQueuePath, description, queueName, transactionMode, RabbitMqMessageContext.InfrastructureType, deadLetterQueuePath, maxReceiveAttempts);
            return this;
        }

        public RabbitMqOptions Build()
        {
            if (_rabbitMqOptions is null)
            {
                throw new ArgumentNullException(nameof(_rabbitMqOptions),
                    $"Use an overload of {nameof(AddRabbitMqOptions)} to configure {typeof(RabbitMqOptions).Name}");
            }

            if (string.IsNullOrWhiteSpace(_rabbitMqOptions.Uri) && string.IsNullOrWhiteSpace(_rabbitMqOptions.HostName))
            {
                throw new ArgumentNullException(nameof(_rabbitMqOptions.HostName), "A connection URI or host name is required.");
            }

            if (string.IsNullOrWhiteSpace(_rabbitMqOptions.MessageBodyType))
            {
                throw new ArgumentNullException(nameof(_rabbitMqOptions.MessageBodyType), "A message body type is required.");
            }

            return _rabbitMqOptions;
        }
    }
}
