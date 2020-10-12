using Chatter.CQRS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// An infrastructure agnostic receiver of brokered messages of type <typeparamref name="TMessage"/>
    /// </summary>
    /// <typeparam name="TMessage">The type of messages the brokered message receiver accepts</typeparam>
    class BrokeredMessageReceiverBackgroundService<TMessage> : BackgroundService where TMessage : class, IMessage
    {
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;
        private readonly ILogger<BrokeredMessageReceiverBackgroundService<TMessage>> _logger;
        private readonly IBrokeredMessageReceiverFactory _receiverFactory;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureReceiver">The message broker infrastructure</param>
        /// <param name="brokeredMessageDetailProvider">Provides routing details to the brokered message receiver</param>
        /// <param name="serviceFactory">THe service scope factory used to create a new scope when a message is received from the messaging infrastructure.</param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiverBackgroundService(IBrokeredMessageDetailProvider brokeredMessageDetailProvider,
                                                        ILogger<BrokeredMessageReceiverBackgroundService<TMessage>> logger,
                                                        IBrokeredMessageReceiverFactory receiverFactory)
        {
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _receiverFactory = receiverFactory ?? throw new ArgumentNullException(nameof(receiverFactory));
        }

        /// <summary>
        /// The receiver should automatically receive brokered messages when the <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> is created
        /// </summary>
        public bool AutoReceiveMessages => _brokeredMessageDetailProvider.AutoReceiveMessages<TMessage>();

        /// <summary>
        /// Describes the receiver. Used to track progress using the 'Via' user property of the <see cref="InboundBrokeredMessage"/>./>
        /// </summary>
        public string Description => _brokeredMessageDetailProvider.GetBrokeredMessageDescription<TMessage>();

        /// <summary>
        /// Gets the name of the path to receive messages.
        /// </summary>
        public string MessageReceiverPath => _brokeredMessageDetailProvider.GetReceiverName<TMessage>();

        ///<inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (AutoReceiveMessages)
            {
                var receiver = _receiverFactory.Create<TMessage>(MessageReceiverPath, Description);
                await receiver.StartReceiver(stoppingToken);
            }
            else
            {
                await Task.CompletedTask;
            }
        }
    }
}
