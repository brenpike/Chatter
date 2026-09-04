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
        private readonly AsyncServiceScope _scope;
        private readonly IBrokeredMessageReceiver<TMessage> _receiver;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="options">The <see cref="ReceiverOptions"/> the brokered message receiver is started with.</param>
        /// <param name="serviceScopeFactory">Creates the DI scope this background service OWNS and resolves its <see cref="IBrokeredMessageReceiver{TMessage}"/> from.</param>
        public BrokeredMessageReceiverBackgroundService(ReceiverOptions options,
                                                        IServiceScopeFactory serviceScopeFactory)
        {
            if (serviceScopeFactory is null)
            {
                throw new ArgumentNullException(nameof(serviceScopeFactory));
            }

            _options = options ?? throw new ArgumentNullException(nameof(options));

            // INVARIANT: the scope is created HERE and disposed at the end of THIS service's own lifetime, so every
            // scoped member of the receiver's graph stays alive for exactly as long as the receiver uses it. A throw
            // from the resolve below is startup-fatal — the host aborts and the scope goes unobserved.
            _scope = serviceScopeFactory.CreateAsyncScope();
            _receiver = _scope.ServiceProvider.GetRequiredService<IBrokeredMessageReceiver<TMessage>>();
        }

        ///<inheritdoc/>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // The default BackgroundService.StartAsync kicks off ExecuteAsync and stores its Task without
            // awaiting it, so any exception ExecuteAsync throws is never observed at host startup — a
            // startup-fatal receiver failure (e.g. the Azure Service Bus cross-entity-transactions guard's
            // InvalidOperationException) would be silently dropped and the host would keep running broken.
            //
            // Resolve the startup-completion signal BEFORE starting the receive loop. The signal lives on the
            // internal IReceiverStartupSignal seam, not the public IBrokeredMessageReceiver<TMessage> surface. The
            // only registered receiver is the concrete BrokeredMessageReceiver<TMessage>, which implements it.
            // Validating the cast here — before base.StartAsync kicks off ExecuteAsync — fails fast on an
            // unsupported receiver so the receive loop is never started outside the go-live/executeTask
            // observation path, leaving nothing stranded when host startup aborts.
            if (_receiver is not IReceiverStartupSignal startupSignal)
            {
                throw new InvalidOperationException(
                    $"Receiver '{_receiver.GetType().Name}' does not implement {nameof(IReceiverStartupSignal)}; host startup cannot gate on receiver go-live.");
            }

            // Start the base BackgroundService (this assigns the ExecuteAsync Task), then await until the
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

            // Wait — without busy-polling IsReceiving — for the first of three ordering signals:
            //   - ReceivingStarted: the receiver went live (IsReceiving became true); startup succeeded.
            //   - executeTask: ExecuteAsync completed/faulted before going live; a fault is startup-fatal.
            //   - cancellationToken: host startup was cancelled.
            var startedSignal = startupSignal.ReceivingStarted;
            var cancellationSignal = Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.WhenAny(startedSignal, executeTask, cancellationSignal).ConfigureAwait(false);

            if (executeTask.IsCompleted && !startedSignal.IsCompleted)
            {
                // ExecuteAsync ended before the receiver signalled go-live. Awaiting re-throws a startup-fatal
                // fault (e.g. the cross-entity guard's InvalidOperationException) so .NET aborts host startup
                // loudly. A clean completion before going live means the receiver shut down during startup with
                // nothing to run, so there is nothing left to await for the lifetime.
                await executeTask.ConfigureAwait(false);
                return;
            }

            // Either the receiver went live (return so ExecuteAsync keeps running for the receiver's lifetime)
            // or startup was cancelled (surface the OperationCanceledException as the prior code did).
            cancellationToken.ThrowIfCancellationRequested();
        }

        ///<inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var _ = await _receiver.StartReceiver(_options, stoppingToken).ConfigureAwait(false);
        }

        ///<inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Unwind the receive loop FIRST, then dispose the scope: the loop still reads the scoped graph while it
            // tears the messaging infrastructure down, so disposing the scope any earlier would recreate the
            // use-after-dispose defect inside shutdown. The disposal is ASYNCHRONOUS because a scoped dependency may
            // implement only IAsyncDisposable, which a synchronous scope disposal refuses. Scope disposal is
            // idempotent, so a later Dispose() is a no-op.
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            await _scope.DisposeAsync().ConfigureAwait(false);
        }

        ///<inheritdoc/>
        public override void Dispose()
        {
            base.Dispose();
            _scope.Dispose();
        }
    }
}
