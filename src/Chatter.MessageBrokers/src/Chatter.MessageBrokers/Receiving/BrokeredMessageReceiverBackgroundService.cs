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
    ///
    /// This service holds NO DI scope of its own. It is handed the scope FACTORY, and the scope the receiver's graph
    /// is resolved from lives for the duration of a single <see cref="ExecuteAsync"/> call, as an
    /// <c>await using</c> local. That removes the whole class of defect in which an owned scope survives as a field
    /// that some terminal path skips releasing, or releases through a synchronous disposal a scoped member of the
    /// graph refuses: a failed resolve and a startup-fatal throw leave the method directly, and a synchronous host
    /// disposal cancels the receive loop so the method leaves on that cancellation. C# binds the release to every one
    /// of those exits.
    ///
    /// What that does NOT buy is ORDERING against a synchronous disposal. This type adds no disposal override, so a
    /// synchronous host/provider disposal runs the inherited <see cref="BackgroundService.Dispose"/>, which cancels
    /// the stopping token and returns WITHOUT awaiting the execute task. On that path the scope is still released
    /// through <c>DisposeAsync</c> - it is never released through a synchronous disposal - but the release completes
    /// on the receive loop's background unwind, after <c>Dispose</c> has already returned.
    /// </summary>
    /// <typeparam name="TMessage">The type of messages the brokered message receiver accepts</typeparam>
    class BrokeredMessageReceiverBackgroundService<TMessage> : BackgroundService where TMessage : class, IMessage
    {
        private readonly ReceiverOptions _options;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        // INVARIANT: completed by ExecuteAsync — with the acquired receiver's go-live signal — only once the receiver
        // graph has actually been acquired. StartAsync unwraps it, so a receiver that could never be acquired leaves
        // the unwrapped signal pending and startup observes the faulted ExecuteAsync Task instead.
        private readonly TaskCompletionSource<Task> _receivingStarted =
            new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="options">The <see cref="ReceiverOptions"/> the brokered message receiver is started with.</param>
        /// <param name="serviceScopeFactory">Creates the DI scope <see cref="ExecuteAsync"/> opens and releases around the receive loop.</param>
        public BrokeredMessageReceiverBackgroundService(ReceiverOptions options,
                                                        IServiceScopeFactory serviceScopeFactory)
        {
            // INVARIANT: nothing is acquired from DI here. Construction takes the scope FACTORY only, so resolving
            // this service can neither build nor strand any part of the receiver's scoped graph.
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        ///<inheritdoc/>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // The default BackgroundService.StartAsync kicks off ExecuteAsync and stores its Task without
            // awaiting it, so any exception ExecuteAsync throws is never observed at host startup — a
            // startup-fatal receiver failure (e.g. the Azure Service Bus cross-entity-transactions guard's
            // InvalidOperationException) would be silently dropped and the host would keep running broken.
            //
            // The startup-completion signal lives on the internal IReceiverStartupSignal seam, not the public
            // IBrokeredMessageReceiver<TMessage> surface, and is only reachable once ExecuteAsync has acquired the
            // receiver from the scope it owns. ExecuteAsync publishes the signal through _receivingStarted, and an
            // unsupported receiver is rejected there — inside the executeTask this method already observes — so a
            // rejection is startup-fatal without the receive loop ever running.
            //
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
            // Unwrap completes only when BOTH the outer publication and the inner go-live signal complete, so a
            // receiver acquisition that never happened leaves this pending and the executeTask fault below fires.
            var startedSignal = _receivingStarted.Task.Unwrap();
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
            // INVARIANT: the scope is a LOCAL of this method, so its release is bound to EVERY exit — a normal
            // return, a throw from the acquisition below, and the cancellation that ends the receive loop. The
            // release is asynchronous because a scoped member of the graph may implement only IAsyncDisposable.
            await using var scope = _serviceScopeFactory.CreateAsyncScope();

            var receiver = scope.ServiceProvider.GetRequiredService<IBrokeredMessageReceiver<TMessage>>();

            // The only registered receiver is the concrete BrokeredMessageReceiver<TMessage>, which implements the
            // seam. Rejecting an unsupported one here — before the receive loop starts — keeps host startup gated on
            // a go-live signal that is guaranteed to exist.
            if (receiver is not IReceiverStartupSignal startupSignal)
            {
                throw new InvalidOperationException(
                    $"Receiver '{receiver.GetType().Name}' does not implement {nameof(IReceiverStartupSignal)}; host startup cannot gate on receiver go-live.");
            }

            _receivingStarted.TrySetResult(startupSignal.ReceivingStarted);

            // INVARIANT: the nesting order IS the teardown order. The subscription (inner) unwinds the receive loop
            // before the scope (outer) releases the graph, because the loop still reads that graph while it tears the
            // messaging infrastructure down — releasing any earlier would recreate the use-after-dispose defect
            // inside shutdown.
            await using var subscription = await receiver.StartReceiver(_options, stoppingToken).ConfigureAwait(false);
        }
    }
}
