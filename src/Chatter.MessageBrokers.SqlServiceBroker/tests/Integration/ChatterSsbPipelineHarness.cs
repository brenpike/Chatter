using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // The reusable end-to-end harness that boots Chatter's REAL DI graph and receiver pump against a SQL
    // Service Broker database (the SqlServiceBrokerFixture container) and routes messages through Chatter's own
    // dispatcher/handler path — NOT raw ADO.NET. Tests Send via the resolved IBrokeredMessageDispatcher and
    // await a RecordingMessageHandler<TMessage> invocation. Mirrors the Azure Service Bus ChatterPipelineHarness.
    //
    // Composition mirrors how a real Chatter app wires SQL Service Broker:
    //   AddChatterCqrs(config) -> AddMessageBrokers(...) -> AddSqlServiceBroker(ssb => <caller delegate>)
    // The caller delegate registers the receivers under test (AddQueueReceiver<T>); Build layers a FINITE
    // receiver timeout and the fixture's app connection string onto the SqlServiceBrokerOptionsBuilder before
    // invoking it.
    //
    // WAITFOR-HANG GUARD: production defaults ReceiverTimeoutInMilliseconds to -1, which makes the receiver's
    // WAITFOR(RECEIVE) block forever, and SqlServiceBrokerReceiver.StopReceiver()/Cancel() is a NO-OP. So this
    // harness (a) sets a FINITE ReceiverTimeoutInMilliseconds so each RECEIVE returns promptly and the pump
    // loops on a cancellable token, (b) exposes ONLY bounded wait helpers that throw on timeout, and (c) on
    // teardown cancels the pump CancellationTokenSource BEFORE stopping the host so a blocked/looping RECEIVE
    // unwinds via token cancellation (the only teardown signal that works).
    public sealed class ChatterSsbPipelineHarness : IAsyncDisposable
    {
        // Finite receiver timeout (milliseconds) handed to the production receiver. Small and finite so a
        // RECEIVE with no message returns an empty result set quickly and the pump loops on the pump token
        // rather than blocking forever (the -1 production default). Kept finite under BOTH the millisecond name
        // and any seconds-interpretation of the SQL WAITFOR parameter.
        private const int FiniteReceiverTimeoutInMilliseconds = 5;

        private readonly ServiceProvider _provider;
        private readonly IReadOnlyList<IHostedService> _hostedServices;
        private readonly CancellationTokenSource _pumpCts;
        private bool _started;

        private ChatterSsbPipelineHarness(
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
        // SqlServiceBrokerOptionsBuilder (e.g. ssb.AddQueueReceiver<MyCommand>("[chatter_ssb_it_target_queue]",
        // deadLetterServicePath: "chatter_ssb_it_deadletter_service")). messageTypes are the IMessage types
        // whose RecordingMessageHandler<TMessage> should be wired into Chatter's handler resolution; a closed
        // IMessageHandler<TMessage> is registered for each so Chatter's dispatcher resolves and invokes it on
        // the real receive path.
        public static ChatterSsbPipelineHarness Build(
            string connectionString,
            Action<SqlServiceBrokerOptionsBuilder> configureReceivers,
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

            // A real host registers logging automatically; this bare ServiceCollection does not. Chatter's
            // receiver graph (the BrokeredMessageReceiverBackgroundService<T> hosted services) depends on
            // ILogger<T>, so register it up front or the hosted-service activation throws.
            services.AddLogging();

            var registry = new HandlerSignalRegistry();
            services.AddSingleton(registry);

            // Point the handler/receiver assembly scan at THIS test assembly so any concrete test handlers are
            // discovered. RecordingMessageHandler<> is open-generic and excluded by Chatter's
            // IsValidMessageHandler scan filter, so it is registered explicitly below.
            var testAssembly = typeof(ChatterSsbPipelineHarness).Assembly;

            services
                .AddChatterCqrs(configuration, testAssembly)
                .AddMessageBrokers(receiverAssemblies: testAssembly)
                .AddSqlServiceBroker(ssb =>
                {
                    // Seed the options with the fixture's app connection string and a FINITE receiver timeout
                    // (the WAITFOR-hang guard), then let the caller register the receivers under test.
                    ssb.AddSqlServiceBrokerOptions(
                        connectionString,
                        receiverTimeoutInMilliseconds: FiniteReceiverTimeoutInMilliseconds);
                    configureReceivers(ssb);
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

            return new ChatterSsbPipelineHarness(provider, hostedServices, pumpCts)
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

        // Sends a command through Chatter's dispatcher to the provisioned target service, stamping the
        // SSBMessageContext headers the SqlServiceBrokerSender reads to BEGIN DIALOG / SEND on the provisioned
        // initiator service + //Chatter contract + //Chatter/BrokeredMessage message type. destinationPath is
        // the BARE target service name (ServiceBrokerProvisioning.TargetServiceName): BeginDialogConversationCommand
        // strips brackets from the target and uses it as "TO SERVICE", so the destination must name the target
        // SERVICE, not the queue. Opens and disposes its own scope around the send.
        public async Task SendAsync<TMessage>(TMessage message) where TMessage : ICommand
        {
            var options = new SendOptions();
            options.WithMessageContext(SSBMessageContext.ServiceName, ServiceBrokerProvisioning.InitiatorServiceName);
            options.WithMessageContext(SSBMessageContext.ServiceContractName, ServiceBrokerProvisioning.ContractName);
            options.WithMessageContext(SSBMessageContext.MessageTypeName, ServiceBrokerProvisioning.MessageTypeName);

            var dispatcher = CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(message, ServiceBrokerProvisioning.TargetServiceName, options: options)
                    .ConfigureAwait(false);
            }
        }

        // The shared signal for TMessage. Configure ThrowOnHandle BEFORE the message is received to exercise
        // Chatter's deadletter path; await Signal.Handled (bounded via WaitForHandledAsync) to observe the
        // handler invocation.
        public HandlerSignal<TMessage> GetSignal<TMessage>() where TMessage : IMessage
            => Signals.GetOrAdd<TMessage>();

        // Bounded wait for a RecordingMessageHandler<TMessage> invocation: returns the captured payload +
        // broker context, or throws TimeoutException so a stalled receive fails fast instead of hanging CI.
        // NEVER an unbounded wait (WAITFOR-hang guard).
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
        // first — callers assert on the returned count so a never-reached threshold fails fast instead of
        // hanging. NEVER an unbounded wait (WAITFOR-hang guard).
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
            // WAITFOR-hang guard: StopReceiver()/Cancel() is a NO-OP, so token cancellation is the ONLY teardown
            // that unwinds a blocked/looping RECEIVE. Cancel the pump token BEFORE stopping the host.
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
                        // disposal of the provider below.
                    }
                }
            }

            await _provider.DisposeAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
        }
    }
}
