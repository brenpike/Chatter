using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // The reusable end-to-end harness that boots Chatter's REAL DI graph and receiver pump against a RabbitMQ
    // broker (the RabbitMqFixture container) and routes messages through Chatter's own dispatcher/handler path —
    // NOT the raw RabbitMQ.Client. Tests Send via the resolved IBrokeredMessageDispatcher and await a
    // RecordingMessageHandler<TMessage> invocation. Mirrors the SQL Service Broker ChatterSsbPipelineHarness.
    //
    // Composition mirrors how a real Chatter app wires RabbitMQ:
    //   AddChatterCqrs(config) -> AddMessageBrokers(...) -> AddRabbitMq(rmq => <seed options + caller delegate>)
    // The caller delegate registers the receivers under test (AddQueueReceiver<T>); Build seeds the fixture's
    // AMQP URI and the scenario's queue type onto the RabbitMqOptions before invoking it.
    internal sealed class ChatterRabbitMqPipelineHarness : IAsyncDisposable
    {
        // Bounds every residual teardown/send await that would otherwise pass CancellationToken.None, so a wedged
        // host-stop or dispatcher send fails fast instead of hanging CI.
        private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

        private readonly ServiceProvider _provider;
        private readonly IReadOnlyList<IHostedService> _hostedServices;
        private readonly CancellationTokenSource _pumpCts;
        private bool _started;

        private ChatterRabbitMqPipelineHarness(
            ServiceProvider provider,
            IReadOnlyList<IHostedService> hostedServices,
            CancellationTokenSource pumpCts)
        {
            _provider = provider;
            _hostedServices = hostedServices;
            _pumpCts = pumpCts;
        }

        // The shared per-message-type signal registry. Tests call GetSignal<TMessage>() to obtain the same
        // HandlerSignal<TMessage> the DI-resolved RecordingMessageHandler<TMessage> reports through.
        public HandlerSignalRegistry Signals { get; private set; }

        // Builds the harness against the fixture's AMQP URI. queueType pins the scenario's work-queue delivery-
        // count strategy (Quorum native x-delivery-count vs Classic x-chatter-delivery-count republish counter).
        // configureReceivers registers the receivers under test on the RabbitMqOptionsBuilder (e.g.
        // rmq.AddQueueReceiver<MyCommand>(workQueueName, deadLetterQueuePath: dlqName, maxReceiveAttempts: 1)).
        // messageTypes are the IMessage types whose RecordingMessageHandler<TMessage> should be wired into
        // Chatter's handler resolution; a closed IMessageHandler<TMessage> is registered for each so Chatter's
        // dispatcher resolves and invokes it on the real receive path.
        public static ChatterRabbitMqPipelineHarness Build(
            string amqpUri,
            QueueType queueType,
            Action<RabbitMqOptionsBuilder> configureReceivers,
            params Type[] messageTypes)
        {
            if (string.IsNullOrWhiteSpace(amqpUri))
            {
                throw new ArgumentException("An AMQP connection URI is required.", nameof(amqpUri));
            }
            if (configureReceivers is null)
            {
                throw new ArgumentNullException(nameof(configureReceivers));
            }

            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            // A real host registers logging automatically; this bare ServiceCollection does not. Chatter's
            // receiver graph (the BrokeredMessageReceiverBackgroundService<T> hosted services) depends on
            // ILogger<T>, so register it up front or the hosted-service activation throws.
            services.AddLogging();

            var registry = new HandlerSignalRegistry();
            services.AddSingleton(registry);

            // Point the handler/receiver assembly scan at THIS test assembly so any concrete test handlers are
            // discovered. RecordingMessageHandler<> is open-generic and excluded by Chatter's
            // IsValidMessageHandler scan filter, so it is registered explicitly below.
            var testAssembly = typeof(ChatterRabbitMqPipelineHarness).Assembly;

            services
                .AddChatterCqrs(configuration, testAssembly)
                .AddMessageBrokers(receiverAssemblies: testAssembly)
                .AddRabbitMq(rmq =>
                {
                    // Seed the options with the fixture's AMQP URI and the scenario's queue type, then let the
                    // caller register the receivers under test. Prefetch is left at the default (1).
                    rmq.AddRabbitMqOptions(uri: amqpUri, queueType: queueType);
                    configureReceivers(rmq);
                });

            // Register a closed RecordingMessageHandler<TMessage> as the IMessageHandler<TMessage> Chatter's
            // dispatcher resolves for each message type under test.
            foreach (var messageType in messageTypes ?? Array.Empty<Type>())
            {
                var handlerInterface = typeof(IMessageHandler<>).MakeGenericType(messageType);
                var handlerImplementation = typeof(RecordingMessageHandler<>).MakeGenericType(messageType);
                services.AddTransient(handlerInterface, handlerImplementation);
            }

            var provider = services.BuildServiceProvider();

            var hostedServices = new List<IHostedService>(provider.GetServices<IHostedService>());
            var pumpCts = new CancellationTokenSource();

            return new ChatterRabbitMqPipelineHarness(provider, hostedServices, pumpCts)
            {
                Signals = registry,
            };
        }

        // Starts the receiver pump in-process. Each receiver's BackgroundService.ExecuteAsync drives the receive
        // loop for the receiver lifetime, but StartAsync returns once ExecuteAsync yields at its first await,
        // returning control to the test. The pump token is passed so DisposeAsync can stop the loop.
        // INVARIANT: idempotent — starting twice is a no-op.
        public async Task StartAsync()
        {
            if (_started)
            {
                return;
            }

            foreach (var hostedService in _hostedServices)
            {
                await hostedService.StartAsync(_pumpCts.Token).ConfigureAwait(false);
            }

            _started = true;
        }

        // Resolves the IBrokeredMessageDispatcher tests use to Send through Chatter. Resolved from a fresh scope
        // each call (the dispatcher is scoped), mirroring how Chatter resolves it per receive.
        public IBrokeredMessageDispatcher CreateDispatcher(out IServiceScope scope)
        {
            scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>();
        }

        // Sends a command through Chatter's dispatcher under the default-exchange convention: destinationPath is
        // the work-queue name and the sender publishes to exchange "" with routing key = the queue name. The
        // RabbitMQ InfrastructureType is stamped explicitly so the dispatcher routes to the RabbitMQ sender even
        // though RabbitMQ is also the harness's only (default) broker. Opens and disposes its own scope.
        public Task SendToQueueAsync<TMessage>(TMessage message, string workQueueName) where TMessage : ICommand
        {
            var options = new SendOptions();
            options.WithMessageContext(MessageContext.InfrastructureType, RabbitMqMessageContext.InfrastructureType);
            return SendAsync(message, workQueueName, options);
        }

        // Sends a command through Chatter's dispatcher with an explicit exchange + routing key override (the
        // WithRabbitMqRouting path): the sender publishes to the supplied exchange with the supplied routing key
        // rather than the default-exchange convention. The override keys are the SAME ones WithRabbitMqRouting
        // stamps, carried here via SendOptions so they flow into the OutboundBrokeredMessage the sender reads.
        public Task SendWithRoutingAsync<TMessage>(TMessage message, string workQueueName, string exchange, string routingKey)
            where TMessage : ICommand
        {
            var options = new SendOptions();
            options.WithMessageContext(MessageContext.InfrastructureType, RabbitMqMessageContext.InfrastructureType);
            options.WithMessageContext(RabbitMqMessageContext.TargetExchange, exchange);
            options.WithMessageContext(RabbitMqMessageContext.RoutingKey, routingKey);
            return SendAsync(message, workQueueName, options);
        }

        private async Task SendAsync<TMessage>(TMessage message, string destinationPath, SendOptions options)
            where TMessage : ICommand
        {
            var dispatcher = CreateDispatcher(out var scope);
            using (scope)
            using (var sendCts = new CancellationTokenSource(TeardownTimeout))
            {
                // Bound the dispatcher send so a wedged publish fails fast. The dispatcher Send overload takes no
                // token, so race it against a finite delay rather than passing CancellationToken.None to an
                // unbounded await.
                var send = dispatcher.Send(message, destinationPath, options: options);
                var completed = await Task.WhenAny(send, Task.Delay(Timeout.Infinite, sendCts.Token)).ConfigureAwait(false);
                if (completed != send)
                {
                    throw new TimeoutException(
                        $"Timed out after {TeardownTimeout} sending a '{typeof(TMessage).Name}' through the dispatcher.");
                }

                await send.ConfigureAwait(false);
            }
        }

        // The shared signal for TMessage. Configure ThrowOnHandle BEFORE the message is received to exercise
        // Chatter's nack/deadletter path; await Signal.Handled (bounded via WaitForHandledAsync) to observe the
        // handler invocation.
        public HandlerSignal<TMessage> GetSignal<TMessage>() where TMessage : IMessage
            => Signals.GetOrAdd<TMessage>();

        // Bounded wait for a RecordingMessageHandler<TMessage> invocation: returns the captured payload +
        // broker context, or throws TimeoutException so a stalled receive fails fast instead of hanging CI.
        public async Task<HandledRecord<TMessage>> WaitForHandledAsync<TMessage>(TimeSpan timeout)
            where TMessage : IMessage
        {
            var handled = GetSignal<TMessage>().Handled;
            var completed = await Task.WhenAny(handled, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != handled)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for a handler invocation of '{typeof(TMessage).Name}'.");
            }

            return await handled.ConfigureAwait(false);
        }

        // Bounded poll until the handler for TMessage has been invoked at least minCount times, returning the
        // observed count. Returns the last observed count (which may be below minCount) if the timeout elapses
        // first — callers assert on the returned count so a never-reached threshold fails fast instead of hanging.
        public async Task<int> WaitForInvocationCountAsync<TMessage>(int minCount, TimeSpan timeout)
            where TMessage : IMessage
        {
            var signal = GetSignal<TMessage>();
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (signal.InvocationCount >= minCount)
                {
                    return signal.InvocationCount;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }

            return signal.InvocationCount;
        }

        public async ValueTask DisposeAsync()
        {
            // Cancel the pump token BEFORE stopping the host so a blocked/looping receive unwinds via token
            // cancellation (the receiver's blocking buffer pull async-parks on this token).
            _pumpCts.Cancel();

            if (_started)
            {
                using var stopCts = new CancellationTokenSource(TeardownTimeout);
                foreach (var hostedService in _hostedServices)
                {
                    try
                    {
                        await hostedService.StopAsync(stopCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Best-effort drain on teardown: a receiver already faulted/cancelled (or a stop that
                        // exceeded TeardownTimeout) must not mask disposal of the provider below.
                    }
                }
            }

            // _provider.DisposeAsync() takes no CancellationToken, so it is left unbounded here (not cancelable).
            await _provider.DisposeAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
        }
    }
}
