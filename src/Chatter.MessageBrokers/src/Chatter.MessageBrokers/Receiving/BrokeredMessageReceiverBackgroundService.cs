using Chatter.CQRS;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly ReceiverOptions _options;
        private readonly IBrokeredMessageReceiver<TMessage> _receiver;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="receiverFactory">Factory that creates <see cref="IBrokeredMessageReceiver"/> for messages of type <typeparamref name="TMessage"/>.</param>
        public BrokeredMessageReceiverBackgroundService(ReceiverOptions options,
                                                        IServiceProvider serviceScopeFactory)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _receiver = serviceScopeFactory.GetRequiredService<IBrokeredMessageReceiver<TMessage>>();
        }

        ///<inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var _ = await _receiver.StartReceiver(_options, stoppingToken).ConfigureAwait(false);
        }
    }
}
