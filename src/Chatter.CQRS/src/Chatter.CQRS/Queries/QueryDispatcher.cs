using Chatter.CQRS.Context;
using Chatter.CQRS.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("Chatter.CQRS.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]
namespace Chatter.CQRS.Queries
{
    /// <summary>
    /// An <see cref="IQueryDispatcher"/> implementation to dispatch <see cref="IQuery"/> and <see cref="IQuery{T}"/> messages.
    /// </summary>
    internal sealed class QueryDispatcher : IQueryDispatcher
    {
        // INVARIANT: this cache is static and holds ONLY stateless invokers. This dispatcher and every
        // IQueryHandler it resolves are registered per scope, so a cached handler instance or a captured
        // IServiceProvider would dispatch through a disposed scope. The provider is passed to the invoker per
        // dispatch and the handler is resolved on every dispatch.
        // INVARIANT: the key is the pair (runtime query type, result type). Keying on the compile-time
        // IQuery<TResult> or on TResult alone would route two different query types to a single handler.
        // INVARIANT: this cache is process-lifetime with no eviction, and every entry strongly roots the
        // caller-supplied query Type for the life of the process. Entry count is bounded by the number of
        // distinct (runtime query type, result type) pairs ever dispatched, not by traffic. A caller that
        // needs a collectible AssemblyLoadContext to unload must dispatch through Query<TQuery, TResult>,
        // which never touches this cache (ADR-0013).
        private static readonly ConcurrentDictionary<(Type QueryType, Type ResultType), object> _invokers = new ConcurrentDictionary<(Type QueryType, Type ResultType), object>();
        private static readonly Func<(Type QueryType, Type ResultType), object> _invokerFactory = CreateInvoker;

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<QueryDispatcher> _logger;

        /// <summary>
        /// The invoker cache, exposed to this assembly's tests so that per-pair reuse across scopes, and the absence
        /// of any cached handler, can be asserted.
        /// </summary>
        internal static IReadOnlyDictionary<(Type QueryType, Type ResultType), object> CachedInvokers => _invokers;

        public QueryDispatcher(IServiceProvider serviceProvider, ILogger<QueryDispatcher> logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

		///<inheritdoc/>
		public Task<TResult> Query<TResult>(IQuery<TResult> query)
			=> Query<TResult>(query, new QueryHandlerContext());
		
		///<inheritdoc/>
		public async Task<TResult> Query<TResult>(IQuery<TResult> query, IQueryHandlerContext queryHandlerContext)
        {
            try
            {
                var invoker = GetOrAddInvoker<TResult>(query.GetType());
                return await invoker.Invoke(_serviceProvider, query, queryHandlerContext);
            }
            catch (Exception e)
            {
                LogDispatchFault(e, query.GetType(), queryHandlerContext);
                throw;
            }
        }

        ///<inheritdoc/>
        public Task<TResult> Query<TQuery, TResult>(TQuery query) where TQuery : class, IQuery<TResult>
			=> Query<TQuery, TResult>(query, new QueryHandlerContext());
        
        ///<inheritdoc/>
        public Task<TResult> Query<TQuery, TResult>(TQuery query, IQueryHandlerContext queryHandlerContext) where TQuery : class, IQuery<TResult>
        {
            // INVARIANT: ADR-0010 R1/R4 (as amended), as CommandDispatcher.Dispatch applies them: the off-guard runs
            // before any argument is constructed and the off path returns the uninstrumented dispatch's own Task. No
            // test pins this for query dispatch.
            if (!ChatterDiagnostics.IsEnabled)
            {
                return DispatchToHandler<TQuery, TResult>(query, queryHandlerContext, handleFault: true);
            }

            return DispatchToHandlerWithDiagnostics<TQuery, TResult>(query, queryHandlerContext);
        }

        private async Task<TResult> DispatchToHandler<TQuery, TResult>(TQuery query, IQueryHandlerContext queryHandlerContext, bool handleFault) where TQuery : class, IQuery<TResult>
        {
            try
            {
                var handler = _serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
                return await handler.Handle(query, queryHandlerContext);
            }
            // INVARIANT: exactly one frame logs a dispatch fault: this one on the diagnostics-off path, the diagnostics
            // wrapper otherwise. Pinned by WhenDispatchingGenericQuery.MustLogTheAsynchronousFaultExactlyOnceWhenDiagnosticsAreEnabled
            // and WhenChatterTracingIsOptedInto.MustWriteExactlyOneErrorRecordWhenTheCallerTokenIsSignalledOnlyAfterTheQueryFaultWasLoggedAsAnError,
            // which go red when the handleFault filter is deleted so both frames log.
            catch (Exception e) when (handleFault)
            {
                LogDispatchFault(e, typeof(TQuery), queryHandlerContext);
                throw;
            }
        }

        private async Task<TResult> DispatchToHandlerWithDiagnostics<TQuery, TResult>(TQuery query, IQueryHandlerContext queryHandlerContext) where TQuery : class, IQuery<TResult>
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            string errorType = null;

            using (var activity = ChatterDiagnostics.StartDispatch<TQuery>(ChatterTelemetryTags.DispatchKinds.Query))
            {
                try
                {
                    return await DispatchToHandler<TQuery, TResult>(query, queryHandlerContext, handleFault: false);
                }
                catch (Exception e)
                {
                    // INVARIANT: the fault is classified once, by LogDispatchFault, and that one verdict decides both the
                    // log level and whether the span and the metric are marked. Pinned by
                    // WhenChatterTracingIsOptedInto.MustMarkTheSpanAsFailedWhenTheCallerTokenIsSignalledOnlyAfterTheQueryFaultWasLoggedAsAnError
                    // and MustMarkTheMeasurementWithAnErrorTypeWhenTheCallerTokenIsSignalledOnlyAfterTheQueryFaultWasLoggedAsAnError,
                    // which go red when this catch re-reads CallerRequestedCancellation.Explains instead of consuming
                    // that verdict. Rationale: ADR-0040.
                    if (LogDispatchFault(e, typeof(TQuery), queryHandlerContext))
                    {
                        // INVARIANT: the span status and the metric's error.type come from the same resolver (ADR-0010 D4).
                        // WhenChatterTracingIsOptedInto.MustMarkTheSpanAndTheMeasurementWithTheSameErrorTypeWhenTheQueryHandlerFails
                        // goes red when errorType is resolved as e.GetType().Name instead; MustLeaveTheSpanStatusUnsetWhenTheCallerCancelledTheQueryDispatch
                        // and MustNotMarkTheMeasurementWithAnErrorTypeWhenTheCallerCancelledTheQueryDispatch go red when
                        // this condition is deleted so a caller-requested cancellation marks both.
                        errorType = ActivityOutcome.ResolveErrorType(e);
                        ActivityOutcome.RecordFailure(activity, e);
                    }

                    throw;
                }
                finally
                {
                    ChatterDiagnostics.RecordDispatchDuration<TQuery>(startTimestamp, ChatterTelemetryTags.DispatchKinds.Query, errorType);
                }
            }
        }

        /// <summary>
        /// Logs a dispatch fault exactly once: at <see cref="LogLevel.Debug"/> when the caller requested the
        /// cancellation that caused it, at <see cref="LogLevel.Error"/> otherwise.
        /// </summary>
        /// <param name="fault">The fault the dispatch raised.</param>
        /// <param name="queryType">The type of the query being dispatched, named in the log record.</param>
        /// <param name="queryHandlerContext">The context the query was dispatched with.</param>
        /// <returns><see langword="true"/> when the fault was logged as a dispatch error, which is the only case in
        /// which telemetry marks the dispatch as failed.</returns>
        private bool LogDispatchFault(Exception fault, Type queryType, IQueryHandlerContext queryHandlerContext)
        {
            // INVARIANT: only a cancellation the caller requested is logged as routine; any other fault, a spontaneous
            // cancellation included, is logged as an error. The caller's token is read here once per fault and nowhere
            // else on the fault path of either Query overload. Pinned by
            // WhenDispatchingGenericQuery.MustLogErrorNotDebugWhenTheCancellationWasNotRequestedByTheCaller and
            // WhenDispatchingStrongTypedQuery.MustLogErrorNotDebugWhenTheCancellationWasNotRequestedByTheCaller, which
            // go red when this condition is replaced by a bare `fault is OperationCanceledException`. Rationale: ADR-0040.
            if (CallerRequestedCancellation.Explains(fault, queryHandlerContext))
            {
                _logger.LogDebug(fault, "Dispatch of query '{QueryType}' was cancelled by the caller.", queryType.Name);
                return false;
            }

            _logger.LogError(fault, "Error dispatching query of type '{QueryType}'", queryType.Name);
            return true;
        }

        private static QueryInvoker<TResult> GetOrAddInvoker<TResult>(Type queryType)
            => (QueryInvoker<TResult>)_invokers.GetOrAdd((queryType, typeof(TResult)), _invokerFactory);

        private static object CreateInvoker((Type QueryType, Type ResultType) key)
            => Activator.CreateInstance(typeof(QueryInvoker<,>).MakeGenericType(key.QueryType, key.ResultType));

        /// <summary>
        /// Resolves and invokes the <see cref="IQueryHandler{TQuery, TResult}"/> for one (query type, result type)
        /// pair, so that a dispatch costs neither a runtime generic type construction nor a DLR call site.
        /// </summary>
        /// <typeparam name="TResult">The return type of the query.</typeparam>
        private abstract class QueryInvoker<TResult>
        {
            internal abstract Task<TResult> Invoke(IServiceProvider serviceProvider, IQuery<TResult> query, IQueryHandlerContext queryHandlerContext);
        }

        /// <summary>
        /// The closed adapter constructed once per (query type, result type) pair. It is stateless: it holds no
        /// handler and no <see cref="IServiceProvider"/>, so a single instance is safe to share across every scope.
        /// </summary>
        /// <typeparam name="TQuery">The runtime type of the query.</typeparam>
        /// <typeparam name="TResult">The return type of the query.</typeparam>
        private sealed class QueryInvoker<TQuery, TResult> : QueryInvoker<TResult> where TQuery : class, IQuery<TResult>
        {
            internal override Task<TResult> Invoke(IServiceProvider serviceProvider, IQuery<TResult> query, IQueryHandlerContext queryHandlerContext)
            {
                var handler = serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
                return handler.Handle((TQuery)query, queryHandlerContext);
            }
        }
    }
}
