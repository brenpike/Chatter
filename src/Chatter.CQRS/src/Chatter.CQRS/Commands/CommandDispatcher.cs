using Chatter.CQRS.Context;
using Chatter.CQRS.Diagnostics;
using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("Chatter.CQRS.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]
namespace Chatter.CQRS.Commands
{
    /// <summary>
    /// An <see cref="IDispatchMessages"/> implementation to dispatch <see cref="ICommand"/> messages.
    /// </summary>
    internal sealed class CommandDispatcher : IDispatchMessages
    {
        private readonly IServiceProvider _serviceFactory;
        private readonly ILogger<CommandDispatcher> _logger;

        public CommandDispatcher(IServiceProvider serviceFactory, ILogger<CommandDispatcher> logger)
        {
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Type DispatchType => typeof(ICommand);

        /// <summary>
        /// Dispatches an <see cref="ICommand"/> to its <see cref="IMessageHandler{TMessage}"/> with additional context.
        /// </summary>
        /// <typeparam name="TMessage">The type of command to be dispatched.</typeparam>
        /// <param name="message">The command to be dispatched.</param>
        /// <param name="messageHandlerContext">The context to be dispatched with <paramref name="message"/>.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <remarks><see cref="ICommand"/> can only have a single handler that will be invoked when 
        /// the <paramref name="message"/> is dispatched by <see cref="IMessageDispatcher"/>.</remarks>
        public Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            // INVARIANT: ADR-0010 R1/R4 — the off-guard is evaluated before any argument is constructed, and the
            // off path returns the original Task from the uninstrumented dispatch, so no async state machine, no
            // timestamp read and no allocation are added when an application has not opted into diagnostics.
            if (!ChatterDiagnostics.IsEnabled)
            {
                return DispatchToHandler(message, messageHandlerContext);
            }

            return DispatchToHandlerWithDiagnostics(message, messageHandlerContext);
        }

        private Task DispatchToHandler<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            try
            {
                var handler = _serviceFactory.GetRequiredService<IMessageHandler<TMessage>>();
                var pipeline = _serviceFactory.GetService<ICommandBehaviorPipeline<TMessage>>();

                if (pipeline == null)
                {
                    _logger.LogTrace($"No command behavior pipeline found. Executing message handler for '{typeof(TMessage)}'.");
                    return handler.Handle(message, messageHandlerContext);
                }
                
                _logger.LogTrace($"Executing command behavior pipeline for '{typeof(TMessage)}'.");
                return pipeline.Execute(message, messageHandlerContext, handler);
            }
            catch (Exception e)
            {
                _logger.LogError($"Error dispatching command of type '{typeof(TMessage).Name}': {e.StackTrace}");
                throw;
            }
        }

        private async Task DispatchToHandlerWithDiagnostics<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            string errorType = null;

            using (var activity = ChatterDiagnostics.StartDispatch<TMessage>(ChatterTelemetryTags.DispatchKinds.Command))
            {
                try
                {
                    await DispatchToHandler(message, messageHandlerContext).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // INVARIANT: the span status and the metric's error.type come from the same resolver, so the
                    // two signals can never disagree about how a dispatch failed (ADR-0010 D4).
                    errorType = ActivityOutcome.ResolveErrorType(e);
                    ActivityOutcome.RecordFailure(activity, e);
                    throw;
                }
                finally
                {
                    ChatterDiagnostics.RecordDispatchDuration<TMessage>(startTimestamp, ChatterTelemetryTags.DispatchKinds.Command, errorType);
                }
            }
        }
    }
}
