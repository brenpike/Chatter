using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Diagnostics
{
    /// <summary>
    /// Serialises every diagnostics test in this assembly onto one xunit collection.
    /// </summary>
    /// <remarks>
    /// This is correctness, not tidiness. A .NET <c>ActivityListener</c> is PROCESS-GLOBAL and the Chatter
    /// source and meter names are fixed literals, so an opted-in test running concurrently with an absence
    /// test would let the absence test observe the opted-in test's .NET listener and fail intermittently.
    /// The definition MUST live in this test assembly: xunit v2 discovers collection definitions only in the
    /// assembly under run, which is why <c>Chatter.Testing.Core</c> deliberately declares none.
    /// </remarks>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class DiagnosticsCollection
    {
        /// <summary>The collection name every diagnostics test class is attributed with.</summary>
        public const string Name = "chatter-diagnostics";
    }

    /// <summary>A Command whose dispatch is observed by the diagnostics tests.</summary>
    public sealed class TracedCommand : ICommand { }

    /// <summary>An Event whose dispatch is observed by the diagnostics tests.</summary>
    public sealed class TracedEvent : IEvent { }

    /// <summary>A Command whose handler always fails, so failure spans and failure metrics can be observed.</summary>
    public sealed class FailingCommand : ICommand { }

    /// <summary>A Command whose handler always faults with an <see cref="OperationCanceledException"/>.</summary>
    public sealed class CancelledCommand : ICommand { }

    /// <summary>An Event whose handler always faults with an <see cref="OperationCanceledException"/>.</summary>
    public sealed class CancelledEvent : IEvent { }

    /// <summary>The exception a <see cref="ThrowingMessageHandler{TMessage}"/> raises.</summary>
    public sealed class DiagnosticsProbeException : Exception
    {
        public DiagnosticsProbeException(string message)
            : base(message)
        { }
    }

    /// <summary>
    /// A Command whose handler always fails with a GENERIC exception type, so tests can observe whether
    /// <c>exception.type</c> is rendered as <see cref="Type.FullName"/> or <see cref="Type.ToString"/> —
    /// the two spellings diverge only for generic types.
    /// </summary>
    public sealed class GenericFailingCommand : ICommand { }

    /// <summary>The GENERIC exception a <see cref="ThrowingGenericMessageHandler{TMessage}"/> raises.</summary>
    public sealed class DiagnosticsProbeException<T> : Exception
    {
        public DiagnosticsProbeException(string message)
            : base(message)
        { }
    }

    /// <summary>
    /// A handler that captures the ambient <see cref="Activity"/> observed while the message was handled, so a
    /// test can tell whether Chatter pushed a span of its own around the handler.
    /// </summary>
    public sealed class AmbientActivityRecordingHandler<TMessage> : IMessageHandler<TMessage> where TMessage : IMessage
    {
        public int InvocationCount { get; private set; }

        public Activity AmbientActivityWhileHandling { get; private set; }

        public Task Handle(TMessage message, IMessageHandlerContext context)
        {
            InvocationCount++;
            AmbientActivityWhileHandling = Activity.Current;
            return Task.CompletedTask;
        }
    }

    /// <summary>A handler that always throws <see cref="Failure"/>, the same instance on every invocation.</summary>
    public sealed class ThrowingMessageHandler<TMessage> : IMessageHandler<TMessage> where TMessage : IMessage
    {
        public DiagnosticsProbeException Failure { get; } = new DiagnosticsProbeException("The handled message failed deliberately.");

        public Task Handle(TMessage message, IMessageHandlerContext context) => throw Failure;
    }

    /// <summary>A handler that always throws a GENERIC <see cref="DiagnosticsProbeException{T}"/>, the same instance on every invocation.</summary>
    public sealed class ThrowingGenericMessageHandler<TMessage> : IMessageHandler<TMessage> where TMessage : IMessage
    {
        public DiagnosticsProbeException<string> Failure { get; } = new DiagnosticsProbeException<string>("The handled message failed deliberately.");

        public Task Handle(TMessage message, IMessageHandlerContext context) => throw Failure;
    }

    /// <summary>
    /// A handler that always throws <see cref="Failure"/>, an <see cref="OperationCanceledException"/>, the same
    /// instance on every invocation, whether or not the dispatch's cancellation token is signalled.
    /// </summary>
    public sealed class ThrowingCancellationHandler<TMessage> : IMessageHandler<TMessage> where TMessage : IMessage
    {
        public OperationCanceledException Failure { get; } = new OperationCanceledException("The handled message was cancelled deliberately.");

        public Task Handle(TMessage message, IMessageHandlerContext context) => throw Failure;
    }

    /// <summary>
    /// A real Message Dispatcher over a real service provider, so the diagnostics tests exercise the whole
    /// dispatch path rather than a mocked stand-in for it.
    /// </summary>
    /// <remarks>
    /// Declared here rather than in its own file because it is shared by all three diagnostics test classes,
    /// which is also exactly the set of classes this file's collection definition serialises.
    /// </remarks>
    public sealed class DiagnosticsDispatchHarness : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        /// <param name="commandDispatcherLogger">The logger the Command dispatcher writes to; <see cref="NullLogger{T}"/> when omitted.</param>
        /// <param name="eventDispatcherLogger">The logger the Event dispatcher writes to; <see cref="NullLogger{T}"/> when omitted.</param>
        internal DiagnosticsDispatchHarness(ILogger<CommandDispatcher> commandDispatcherLogger = null, ILogger<EventDispatcher> eventDispatcherLogger = null)
        {
            var resolvedCommandDispatcherLogger = commandDispatcherLogger ?? NullLogger<CommandDispatcher>.Instance;
            var resolvedEventDispatcherLogger = eventDispatcherLogger ?? NullLogger<EventDispatcher>.Instance;

            CommandHandler = new AmbientActivityRecordingHandler<TracedCommand>();
            EventMessageHandler = new AmbientActivityRecordingHandler<TracedEvent>();
            FailingCommandHandler = new ThrowingMessageHandler<FailingCommand>();
            GenericFailingCommandHandler = new ThrowingGenericMessageHandler<GenericFailingCommand>();
            CancelledCommandHandler = new ThrowingCancellationHandler<CancelledCommand>();
            CancelledEventHandler = new ThrowingCancellationHandler<CancelledEvent>();

            var services = new ServiceCollection();
            services.AddSingleton<IMessageHandler<TracedCommand>>(CommandHandler);
            services.AddSingleton<IMessageHandler<TracedEvent>>(EventMessageHandler);
            services.AddSingleton<IMessageHandler<FailingCommand>>(FailingCommandHandler);
            services.AddSingleton<IMessageHandler<GenericFailingCommand>>(GenericFailingCommandHandler);
            services.AddSingleton<IMessageHandler<CancelledCommand>>(CancelledCommandHandler);
            services.AddSingleton<IMessageHandler<CancelledEvent>>(CancelledEventHandler);
            services.AddSingleton<IDispatchMessages>(provider => new CommandDispatcher(provider, resolvedCommandDispatcherLogger));
            services.AddSingleton<IDispatchMessages>(provider => new EventDispatcher(provider, resolvedEventDispatcherLogger));
            services.AddSingleton<IMessageDispatcherProvider, MessageDispatcherProvider>();
            services.AddSingleton<IExternalDispatcher, NoOpExternalDispatcher>();
            services.AddSingleton<IMessageDispatcher, MessageDispatcher>();

            _serviceProvider = services.BuildServiceProvider();
            Dispatcher = _serviceProvider.GetRequiredService<IMessageDispatcher>();
        }

        /// <summary>The Message Dispatcher under observation.</summary>
        public IMessageDispatcher Dispatcher { get; }

        public AmbientActivityRecordingHandler<TracedCommand> CommandHandler { get; }

        public AmbientActivityRecordingHandler<TracedEvent> EventMessageHandler { get; }

        public ThrowingMessageHandler<FailingCommand> FailingCommandHandler { get; }

        public ThrowingGenericMessageHandler<GenericFailingCommand> GenericFailingCommandHandler { get; }

        public ThrowingCancellationHandler<CancelledCommand> CancelledCommandHandler { get; }

        public ThrowingCancellationHandler<CancelledEvent> CancelledEventHandler { get; }

        public Task DispatchCommand() => Dispatcher.Dispatch(new TracedCommand());

        public Task DispatchEvent() => Dispatcher.Dispatch(new TracedEvent());

        public Task DispatchFailingCommand() => Dispatcher.Dispatch(new FailingCommand());

        public Task DispatchGenericFailingCommand() => Dispatcher.Dispatch(new GenericFailingCommand());

        /// <summary>Dispatches a <see cref="CancelledCommand"/> under a context carrying <paramref name="callerToken"/>.</summary>
        public Task DispatchCancelledCommand(CancellationToken callerToken)
            => Dispatcher.Dispatch(new CancelledCommand(), new MessageHandlerContext(callerToken));

        /// <summary>Dispatches a <see cref="CancelledEvent"/> under a context carrying <paramref name="callerToken"/>.</summary>
        public Task DispatchCancelledEvent(CancellationToken callerToken)
            => Dispatcher.Dispatch(new CancelledEvent(), new MessageHandlerContext(callerToken));

        public void Dispose() => _serviceProvider.Dispose();
    }

    /// <summary>
    /// A logger that records every entry at <see cref="LogLevel.Debug"/> or above and signals
    /// <see cref="CancellationTokenSource"/> on its FIRST <see cref="LogLevel.Error"/> entry, so a test can make the
    /// caller's token become signalled in the window between a dispatch fault being logged and being marked on
    /// telemetry.
    /// </summary>
    /// <typeparam name="TCategory">The logger category.</typeparam>
    public sealed class CancelOnFirstErrorLogger<TCategory> : ILogger<TCategory>
    {
        private readonly List<(LogLevel level, string message, Exception exception)> _loggedEntries = new List<(LogLevel level, string message, Exception exception)>();
        private bool _hasSignalledCancellation;

        public CancelOnFirstErrorLogger(CancellationTokenSource cancellationSource)
        {
            CancellationSource = cancellationSource ?? throw new ArgumentNullException(nameof(cancellationSource));
        }

        /// <summary>The source cancelled on the first <see cref="LogLevel.Error"/> entry, and never on any other level.</summary>
        public CancellationTokenSource CancellationSource { get; }

        /// <summary>Every entry recorded, in the order it was logged.</summary>
        public IReadOnlyList<(LogLevel level, string message, Exception exception)> LoggedEntries => _loggedEntries;

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _loggedEntries.Add((logLevel, formatter(state, exception), exception));

            if (logLevel == LogLevel.Error && !_hasSignalledCancellation)
            {
                _hasSignalledCancellation = true;
                CancellationSource.Cancel();
            }
        }
    }
}
