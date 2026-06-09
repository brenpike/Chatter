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
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // The default BackgroundService.StartAsync kicks off ExecuteAsync and stores its Task without
            // awaiting it, so any exception ExecuteAsync throws is never observed at host startup — a
            // startup-fatal receiver failure (e.g. the Azure Service Bus cross-entity-transactions guard's
            // InvalidOperationException) would be silently dropped and the host would keep running broken.
            //
            // Start the base BackgroundService first (this assigns the ExecuteAsync Task), then await until the
            // receiver either:
            //   - goes live (IsReceiving == true) — the startup phase succeeded; return so the receive loop
            //     keeps running for the receiver's lifetime, and
            //   - or its ExecuteAsync Task faults before going live — a startup-fatal failure; surface it from
            //     StartAsync so .NET aborts host startup loudly.
            await base.StartAsync(cancellationToken).ConfigureAwait(false);

            var executeTask = this.ExecuteTask;

            // ExecuteTask is null only if ExecuteAsync completed synchronously; in that case there is nothing
            // further to observe for startup.
            if (executeTask is null)
            {
                return;
            }

            while (!_receiver.IsReceiving)
            {
                if (executeTask.IsCompleted)
                {
                    // Faulted before going live => startup-fatal. Awaiting re-throws the original exception
                    // (e.g. the cross-entity guard's InvalidOperationException) so host startup aborts.
                    // A clean completion before going live means the receiver shut down during startup with
                    // nothing to run, so there is nothing left to await for the lifetime.
                    await executeTask.ConfigureAwait(false);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        ///<inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var _ = await _receiver.StartReceiver(_options, stoppingToken).ConfigureAwait(false);
        }
    }
}
