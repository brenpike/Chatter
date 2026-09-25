using Chatter.CQRS.Context;
using Chatter.CQRS.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("Chatter.CQRS.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]
namespace Chatter.CQRS.Events
{
    /// <summary>
    /// An <see cref="IDispatchMessages"/> implementation to dispatch <see cref="IEvent"/> messages.
    /// </summary>
    internal sealed class EventDispatcher : IDispatchMessages
    {
        private readonly IServiceProvider _serviceFactory;
        private readonly ILogger<EventDispatcher> _logger;

        public EventDispatcher(IServiceProvider serviceFactory, ILogger<EventDispatcher> logger)
        {
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Type DispatchType => typeof(IEvent);

        /// <summary>
        /// Dispatches an <see cref="IEvent"/> to all <see cref="IMessageHandler{TMessage}"/> with additional context.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to be dispatched.</typeparam>
        /// <param name="message">The event to be dispatched.</param>
        /// <param name="messageHandlerContext">The context to be dispatched with <paramref name="message"/>.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <remarks>Each <see cref="IMessageHandler{TMessage}"/> resolved for <typeparamref name="TMessage"/> is
        /// awaited in resolution order. The first handler that throws propagates out of <c>Dispatch</c>: the
        /// dispatcher logs the exception once and rethrows it unchanged, and no subsequent handler is invoked
        /// (ADR-0012). The dispatcher's one <c>LogError</c> call is not always the only one Chatter makes for a
        /// failure. When the event arrived through a <c>BrokeredMessageReceiver</c>, the receiver logs the rethrown
        /// exception again before rethrowing it in turn, so the exception from a failed dispatch of a
        /// broker-delivered event is passed to <c>LogError</c> at least twice: once by <c>EventDispatcher</c> and at
        /// least once more by <c>BrokeredMessageReceiver</c>. When an event is dispatched directly through
        /// <see cref="IMessageDispatcher"/>, with no receiver around the dispatch, the dispatcher's one
        /// <c>LogError</c> call is the only one Chatter makes for that dispatch. A cancellation the caller requested —
        /// an <see cref="OperationCanceledException"/> raised while the token on <paramref name="messageHandlerContext"/>
        /// is signalled — is not a failed dispatch: the dispatcher makes one <c>LogDebug</c> call for it in place of
        /// the <c>LogError</c> call and rethrows it unchanged (ADR-0040). When such a cancellation instead reaches
        /// <c>BrokeredMessageReceiver</c> because its receive loop is being stopped, the receiver makes its own
        /// <c>LogDebug</c> call for it, once, at the dispatch seam; the worker's error ladder then swallows the
        /// exception without a further record (ADR-0010 D11; ADR-0040). These count the calls Chatter makes,
        /// not the records an application sees: whether a call produces a record, and how many, is decided by the log
        /// levels and logging providers the application configures.
        /// Handlers are resolved from the service provider by event type, not by the delivery that triggered the
        /// dispatch. A second broker subscription or queue for the same event in the same host therefore does not
        /// isolate one subscriber: each delivery dispatches into the same handler set in the same order, stopping at
        /// the first handler that throws, so every delivery that reaches a sibling handler invokes it, duplicating its
        /// side effects across those deliveries. A separate delivery is necessary but not sufficient. A subscriber
        /// runs independently of its siblings only when it has its own delivery — its own broker subscription or
        /// queue — and is dispatched by a separate endpoint or host whose service provider registers that
        /// subscriber as the only handler for the event.</remarks>
        public Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            // INVARIANT: ADR-0010 R1/R4 — the off-guard is evaluated before any argument is constructed, and the
            // off path returns the original Task from the uninstrumented dispatch. The diagnostics locals live in
            // the instrumented method alone, so an application that has not opted in keeps the same single async
            // state machine it had before instrumentation and pays no extra allocation, timestamp or string work.
            if (!ChatterDiagnostics.IsEnabled)
            {
                return DispatchToHandlers(message, messageHandlerContext, handleFault: true);
            }

            return DispatchToHandlersWithDiagnostics(message, messageHandlerContext);
        }

        private async Task DispatchToHandlers<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, bool handleFault) where TMessage : IMessage
        {
            try
            {
                var handlers = _serviceFactory.GetServices<IMessageHandler<TMessage>>();
                foreach (var handler in handlers)
                {
                    await handler.Handle(message, messageHandlerContext).ConfigureAwait(false);

                    // INVARIANT: the guard stays inside the loop so the trace is still written once per handler.
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Invoked event handler for '{MessageType}'.", MessageTypeNames<TMessage>.Display);
                    }
                }
            }
            // INVARIANT: exactly one frame logs a dispatch fault: this one on the diagnostics-off path, the diagnostics
            // wrapper otherwise. Pinned by WhenChatterTracingIsOptedInto.MustWriteExactlyOneErrorRecordWhenTheCallerTokenIsSignalledOnlyAfterTheEventFaultWasLoggedAsAnError,
            // which goes red when the handleFault filter is deleted so both frames log.
            catch (Exception e) when (handleFault)
            {
                LogDispatchFault<TMessage>(e, messageHandlerContext);
                throw;
            }
        }

        private async Task DispatchToHandlersWithDiagnostics<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            string errorType = null;

            using (var activity = ChatterDiagnostics.StartDispatch<TMessage>(ChatterTelemetryTags.DispatchKinds.Event))
            {
                try
                {
                    await DispatchToHandlers(message, messageHandlerContext, handleFault: false).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // INVARIANT: the fault is classified once, by LogDispatchFault, and that one verdict decides both the
                    // log level and whether the span and the metric are marked, so a caller token signalled after the log
                    // cannot leave an Error record beside an unmarked span or metric. Pinned by
                    // WhenChatterTracingIsOptedInto.MustMarkTheSpanAsFailedWhenTheCallerTokenIsSignalledOnlyAfterTheEventFaultWasLoggedAsAnError
                    // and MustMarkTheMeasurementWithAnErrorTypeWhenTheCallerTokenIsSignalledOnlyAfterTheEventFaultWasLoggedAsAnError,
                    // which go red when this catch re-reads CallerRequestedCancellation.Explains instead of consuming
                    // that verdict. Rationale: ADR-0040.
                    if (LogDispatchFault<TMessage>(e, messageHandlerContext))
                    {
                        // INVARIANT: the span status and the metric's error.type are set together from the same
                        // resolver, so the two signals cannot disagree about how a dispatch failed (ADR-0010 D4).
                        // WhenChatterTracingIsOptedInto.MustMarkTheSpanAndTheMeasurementAsFailedWhenTheEventCancellationWasNotRequestedByTheCaller
                        // goes red when errorType is resolved as e.GetType().Name instead; MustNotMarkTheSpanAsFailedWhenTheCallerCancelledTheEventDispatch
                        // and MustRecordOneDispatchDurationWithoutAnErrorTypeWhenTheCallerCancelledTheEventDispatch go red
                        // when this condition is deleted so a caller-requested cancellation marks both.
                        errorType = ActivityOutcome.ResolveErrorType(e);
                        ActivityOutcome.RecordFailure(activity, e);
                    }

                    throw;
                }
                finally
                {
                    ChatterDiagnostics.RecordDispatchDuration<TMessage>(startTimestamp, ChatterTelemetryTags.DispatchKinds.Event, errorType);
                }
            }
        }

        /// <summary>
        /// Logs a dispatch fault exactly once: at <see cref="LogLevel.Debug"/> when the caller requested the
        /// cancellation that caused it, at <see cref="LogLevel.Error"/> otherwise.
        /// </summary>
        /// <typeparam name="TMessage">The compile-time type of the event being dispatched.</typeparam>
        /// <param name="fault">The fault the dispatch raised.</param>
        /// <param name="messageHandlerContext">The context the event was dispatched with.</param>
        /// <returns><see langword="true"/> when the fault was logged as a dispatch error, which is the only case in
        /// which telemetry marks the dispatch as failed.</returns>
        private bool LogDispatchFault<TMessage>(Exception fault, IMessageHandlerContext messageHandlerContext)
        {
            // INVARIANT: only a cancellation the caller requested is logged as routine; any other fault, a spontaneous
            // cancellation included, is logged as an error. The caller's token is read here once per fault and nowhere
            // else on the fault path. Pinned by WhenDispatching.MustLogErrorNotDebugWhenTheCancellationWasNotRequestedByTheCaller,
            // which goes red when this condition is replaced by a bare `fault is OperationCanceledException`. Rationale: ADR-0040.
            if (CallerRequestedCancellation.Explains(fault, messageHandlerContext))
            {
                _logger.LogDebug(fault, "Dispatch of event '{MessageType}' was cancelled by the caller.", MessageTypeNames<TMessage>.Name);
                return false;
            }

            _logger.LogError(fault, "Error dispatching event of type '{MessageType}'.", MessageTypeNames<TMessage>.Name);
            return true;
        }

        /// <summary>
        /// Names computed once per closed generic, so a dispatch never builds a type name.
        /// </summary>
        /// <typeparam name="TMessage">The compile-time type of the message being dispatched.</typeparam>
        private static class MessageTypeNames<TMessage>
        {
            /// <summary>
            /// The type rendered exactly as an interpolated <see cref="Type"/> renders it, so a trace
            /// message reads identically for a constructed generic message as for a simple one.
            /// </summary>
            internal static readonly string Display = typeof(TMessage).ToString();
            internal static readonly string Name = typeof(TMessage).Name;
        }
    }
}
