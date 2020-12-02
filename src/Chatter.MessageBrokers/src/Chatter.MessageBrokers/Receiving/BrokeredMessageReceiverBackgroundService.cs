using Chatter.CQRS;
using Microsoft.Extensions.Hosting;
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
        private readonly IBrokeredMessageReceiverFactory _receiverFactory;
        private readonly ReceiverOptions _options;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="brokeredMessageDetailProvider">Provides routing details to the brokered message receiver</param>
        /// <param name="receiverFactory">Factory that creates <see cref="IBrokeredMessageReceiver"/> for messages of type <typeparamref name="TMessage"/>.</param>
        public BrokeredMessageReceiverBackgroundService(ReceiverOptions options,
                                                        IBrokeredMessageReceiverFactory receiverFactory)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _receiverFactory = receiverFactory ?? throw new ArgumentNullException(nameof(receiverFactory));
        }

        ///<inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var receiver = _receiverFactory.Create<TMessage>(_options);
            await using var _ = await receiver.StartReceiver(stoppingToken).ConfigureAwait(false);
        }
    }
}
