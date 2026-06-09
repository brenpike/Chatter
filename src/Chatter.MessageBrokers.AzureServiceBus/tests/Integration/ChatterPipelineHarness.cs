using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // The reusable end-to-end harness that boots Chatter's REAL DI graph and receiver pump against an Azure
    // Service Bus connection string (emulator or real namespace) and routes messages through Chatter's own
    // dispatcher/handler path — NOT the raw Azure SDK. Tests Send/Publish via the resolved
    // IBrokeredMessageDispatcher and await a RecordingMessageHandler<TMessage> invocation; the raw SDK may
    // appear only at test edges (seeding/peeking), never as the system under test.
    //
    // Composition mirrors how a real Chatter app wires Azure Service Bus:
    //   AddChatterCqrs(config) -> AddMessageBrokers(...) -> AddAzureServiceBus(sb => <caller delegate>)
    // The caller delegate registers the receivers under test (AddQueueReceiver<T>/AddTopicSubscription<T>),
    // and Build(...) layers the connection string onto the ServiceBusOptionsBuilder before invoking it so the
    // shared EnableCrossEntityTransactions ServiceBusClient targets the supplied namespace.
    //
    // Pump lifecycle: receivers are registered as IHostedService singletons
    // (BrokeredMessageReceiverBackgroundService<T>). StartAsync starts each; its BackgroundService.ExecuteAsync
    // awaits StartReceiver for the receiver lifetime, so StartAsync returns to the test once the receive loop
    // yields. DisposeAsync cancels the linked pump token, stops the hosted services, and async-disposes the
    // provider so the shared ServiceBusClient (IAsyncDisposable) is torn down without a sync-dispose throw.
    public sealed class ChatterPipelineHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IReadOnlyList<IHostedService> _hostedServices;
        private readonly CancellationTokenSource _pumpCts;
        private bool _started;

        private ChatterPipelineHarness(
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

        // Builds the harness. configureReceivers registers the receivers under test on the
        // ServiceBusOptionsBuilder (e.g. sb.AddQueueReceiver<MyCommand>("chatter.roundtrip")). messageTypes
        // are the IMessage types whose RecordingMessageHandler<TMessage> should be wired into Chatter's
        // handler resolution; a closed IMessageHandler<TMessage> is registered for each so Chatter's
        // command/event dispatcher resolves and invokes it on the real receive path.
        public static ChatterPipelineHarness Build(
            string connectionString,
            Action<ServiceBusOptionsBuilder> configureReceivers,
            params Type[] messageTypes)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A connection string is required.", nameof(connectionString));
            }
            if (configureReceivers is null)
            {
                throw new ArgumentNullException(nameof(configureReceivers));
            }

            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            // A real host (Host.CreateDefaultBuilder / WebApplication) registers logging automatically; this
            // bare ServiceCollection does not. Chatter's receiver graph (MessagingInfrastructureProvider and
            // the BrokeredMessageReceiverBackgroundService<T> hosted services it backs) depends on ILogger<T>,
            // so without AddLogging the hosted-service activation throws "Unable to resolve service for type
            // ILogger`1[...]". Register it up front so the whole Chatter graph resolves.
            services.AddLogging();

            var registry = new HandlerSignalRegistry();
            services.AddSingleton(registry);

            // Point the handler/receiver assembly scan at THIS test assembly so any concrete test handlers are
            // discovered. RecordingMessageHandler<> is open-generic and excluded by Chatter's IsValidMessageHandler
            // scan filter, so it is registered explicitly below rather than relying on the scan.
            var testAssembly = typeof(ChatterPipelineHarness).Assembly;

            services
                .AddChatterCqrs(configuration, testAssembly)
                .AddMessageBrokers(receiverAssemblies: testAssembly)
                .AddAzureServiceBus(sb =>
                {
                    sb.WithConnectionString(connectionString);
                    configureReceivers(sb);
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

            var harness = new ChatterPipelineHarness(provider, hostedServices, pumpCts)
            {
                Signals = registry,
            };
            return harness;
        }

        // Starts the receiver pump in-process. Each receiver's BackgroundService.ExecuteAsync awaits
        // StartReceiver (and thus the receive loop) for the receiver lifetime, but StartAsync returns once
        // ExecuteAsync yields at its first await, returning control to the test. The pump token is passed so
        // DisposeAsync can stop the loop. INVARIANT: idempotent — starting twice is a no-op.
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

        // Resolves the IBrokeredMessageDispatcher tests use to Send/Publish through Chatter. Resolved from a
        // fresh scope each call (the dispatcher is scoped), mirroring how Chatter resolves it per receive.
        public IBrokeredMessageDispatcher CreateDispatcher(out IServiceScope scope)
        {
            scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>();
        }

        // The shared signal for TMessage. Configure ThrowOnHandle BEFORE the message is received to exercise
        // Chatter's deadletter path; await Signal.Handled (bounded via WaitForHandledAsync) to observe the
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
        // observed count. Returns the last observed count (which may be below minCount) if the timeout
        // elapses first — callers assert on the returned count so a never-reached threshold fails fast
        // instead of hanging. Used to observe PeekLock redelivery (count climbs past 1) versus a single
        // ReceiveAndDelete delivery.
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
            _pumpCts.Cancel();

            if (_started)
            {
                foreach (var hostedService in _hostedServices)
                {
                    try
                    {
                        await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Best-effort drain on teardown: a receiver already faulted/cancelled must not mask
                        // disposal of the provider (and the shared ServiceBusClient) below.
                    }
                }
            }

            // INVARIANT: async disposal — the shared ServiceBusClient is IAsyncDisposable and throws on
            // synchronous Dispose, so the provider must be disposed via DisposeAsync.
            await _provider.DisposeAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
        }
    }
}
