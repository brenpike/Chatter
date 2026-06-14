using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.SqlChangeFeed.DependencyInjection;
using Chatter.SqlChangeFeed.Scripts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // The reusable end-to-end harness that boots Chatter's REAL DI graph + change-feed receiver pump against a
    // SQL Server database (the SqlChangeFeedFixture container) and drives the PRODUCTION change-feed path:
    // UseChangeFeedSqlMigrations installs the trigger + Service Broker objects, a row mutation fires the trigger,
    // and the ChangeFeedReceiver dispatches RowInserted/RowUpdated/RowDeleted events through Chatter to a
    // DI-resolved RecordingChangeFeedHandler. Tests await those handler invocations. Mirrors the SQL Service
    // Broker integration ChatterSsbPipelineHarness.
    //
    // Composition mirrors how a real Chatter app wires the change feed:
    //   AddChatterCqrs(config, testAssembly) -> AddMessageBrokers(receiverAssemblies: testAssembly)
    //     -> AddSqlChangeFeed<TRow>(connStr, db, table, options)
    //
    // WAITFOR-HANG GUARD (inherited from the SQL Service Broker receiver the change feed runs on): the production
    // ReceiverTimeoutInMilliseconds default is -1, which makes the receiver's WAITFOR(RECEIVE) block forever, and
    // StopReceiver()/Cancel() is a NO-OP. So this harness (a) sets a FINITE receiver timeout via the options
    // builder so each RECEIVE returns promptly and the pump loops on a cancellable token, (b) exposes ONLY bounded
    // wait helpers (through the signal registry) that throw on timeout, and (c) on teardown cancels the pump
    // CancellationTokenSource BEFORE stopping the host so a blocked/looping RECEIVE unwinds via token cancellation.
    internal sealed class ChatterChangeFeedHarness<TRow> : IAsyncDisposable
        where TRow : class, IMessage, new()
    {
        // Finite receiver timeout (milliseconds) handed to the production receiver via the options builder. Small
        // and finite so a RECEIVE with no message returns quickly and the pump loops on the pump token rather than
        // blocking forever (the -1 production default).
        private const int FiniteReceiverTimeoutInMilliseconds = 5;

        // Bounds the best-effort StopAsync drain on teardown so a wedged host-stop fails fast instead of hanging
        // CI. This does NOT replace the WAITFOR-hang guard: the pump-token cancellation in DisposeAsync is still
        // what unwinds a blocked RECEIVE; this is a finite ceiling on the drain.
        private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

        // Bounds the production migration install so a wedged DDL (e.g. a non-rollback ENABLE_BROKER blocked
        // behind another session) fails fast rather than hanging the test. The install is genuinely async and
        // cancellable; a bounded linked token cancels the await when this ceiling is exceeded.
        private static readonly TimeSpan MigrationTimeout = TimeSpan.FromSeconds(60);

        private readonly ServiceProvider _provider;
        private readonly IReadOnlyList<IHostedService> _hostedServices;
        private readonly CancellationTokenSource _pumpCts;
        private bool _started;

        private ChatterChangeFeedHarness(
            ServiceProvider provider,
            IReadOnlyList<IHostedService> hostedServices,
            CancellationTokenSource pumpCts,
            ChangeFeedSignalRegistry signals)
        {
            _provider = provider;
            _hostedServices = hostedServices;
            _pumpCts = pumpCts;
            Signals = signals;
        }

        // The shared per-event-type signal registry. Tests use it to await change-feed event dispatches and to
        // poll invocation counts (re-arm), all through bounded waits that throw on timeout.
        public ChangeFeedSignalRegistry Signals { get; }

        // Builds the harness over the SqlChangeFeedFixture's app database. connectionString must already point at
        // the watched database (InitialCatalog set); databaseName names that database for the migration. tableName
        // is the watched table (created by ChangeFeedTableProvisioning before RunMigrationsAsync). A closed
        // RecordingChangeFeedHandler<RowXEvent<TRow>> is registered explicitly for each emitted change event so
        // Chatter's dispatcher resolves and invokes it on the real change-feed receive path (the assembly scan
        // excludes the open-generic handler).
        public static ChatterChangeFeedHarness<TRow> Build(
            string connectionString,
            string databaseName,
            string tableName)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A connection string is required.", nameof(connectionString));
            }
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("A database name is required.", nameof(databaseName));
            }
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException("A table name is required.", nameof(tableName));
            }

            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            // A real host registers logging automatically; this bare ServiceCollection does not. Chatter's
            // receiver graph (the BrokeredMessageReceiverBackgroundService<T> hosted services) depends on
            // ILogger<T>, so register it up front or the hosted-service activation throws.
            services.AddLogging();

            var registry = new ChangeFeedSignalRegistry();
            services.AddSingleton(registry);

            // Point the handler/receiver assembly scan at THIS test assembly. RecordingChangeFeedHandler<> is
            // open-generic and excluded by Chatter's scan filter, so the closed handlers are registered below.
            var testAssembly = typeof(ChatterChangeFeedHarness<TRow>).Assembly;

            services
                .AddChatterCqrs(configuration, testAssembly)
                .AddMessageBrokers(receiverAssemblies: testAssembly)
                .AddSqlChangeFeed<TRow>(
                    connectionString,
                    databaseName,
                    tableName,
                    options => options.WithReceiverTimeoutInMilliseconds(FiniteReceiverTimeoutInMilliseconds));

            // Register a closed RecordingChangeFeedHandler<TEvent> as the IMessageHandler<TEvent> Chatter's
            // dispatcher resolves for each emitted change-feed event type.
            RegisterRecordingHandler<RowInsertedEvent<TRow>>(services);
            RegisterRecordingHandler<RowUpdatedEvent<TRow>>(services);
            RegisterRecordingHandler<RowDeletedEvent<TRow>>(services);

            var provider = services.BuildServiceProvider();

            var hostedServices = new List<IHostedService>(provider.GetServices<IHostedService>());
            var pumpCts = new CancellationTokenSource();

            return new ChatterChangeFeedHarness<TRow>(provider, hostedServices, pumpCts, registry);
        }

        private static void RegisterRecordingHandler<TEvent>(IServiceCollection services) where TEvent : IMessage
            => services.AddTransient<IMessageHandler<TEvent>, RecordingChangeFeedHandler<TEvent>>();

        // Runs the PRODUCTION change-feed migration over the configured row type: resolves ISqlDependencyManager
        // <TRow> via UseChangeFeedSqlMigrationsAsync<TRow> and installs the trigger + Service Broker objects +
        // install/uninstall stored procs. Bounded so a wedged install DDL fails fast: the install is genuinely
        // async and cancellable, so a bounded token cancels the await and the OperationCanceledException is
        // surfaced as TimeoutException to preserve the timeout contract.
        public async Task RunMigrationsAsync()
        {
            using var migrationCts = new CancellationTokenSource(MigrationTimeout);
            try
            {
                await _provider.UseChangeFeedSqlMigrationsAsync<TRow>(migrationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (migrationCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out after {MigrationTimeout} running the change-feed migration for '{typeof(TRow).Name}'.");
            }
        }

        // Runs the PRODUCTION change-feed uninstall: resolves ISqlDependencyManager<TRow> and executes the
        // uninstall stored proc, dropping the trigger + Service Broker objects. Bounded for the same reason as
        // RunMigrationsAsync.
        public async Task UninstallAsync()
        {
            var uninstallProcName = $"{ChatterServiceBrokerConstants.ChatterUninstallChangeFeedPrefix}{typeof(TRow).Name}";

            using var uninstallCts = new CancellationTokenSource(MigrationTimeout);
            using var scope = _provider.CreateScope();
            var sdm = scope.ServiceProvider.GetRequiredService<ISqlDependencyManager<TRow>>();

            try
            {
                await sdm.UninstallSqlDependencies(uninstallProcName, uninstallCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (uninstallCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out after {MigrationTimeout} running the change-feed uninstall for '{typeof(TRow).Name}'.");
            }
        }

        // Starts the receiver pump in-process on the pump token. The production BackgroundService.StartAsync gates
        // on receiver go-live, so this returns once the receive loop is live (or surfaces a startup-fatal fault).
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

        public async ValueTask DisposeAsync()
        {
            // WAITFOR-hang guard: StopReceiver()/Cancel() is a NO-OP, so token cancellation is the ONLY teardown
            // that unwinds a blocked/looping RECEIVE. Cancel the pump token BEFORE stopping the host.
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

            await _provider.DisposeAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
        }
    }
}
