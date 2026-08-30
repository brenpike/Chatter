using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics;
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

    /// <summary>The exception a <see cref="ThrowingMessageHandler{TMessage}"/> raises.</summary>
    public sealed class DiagnosticsProbeException : Exception
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

        public DiagnosticsDispatchHarness()
        {
            CommandHandler = new AmbientActivityRecordingHandler<TracedCommand>();
            EventMessageHandler = new AmbientActivityRecordingHandler<TracedEvent>();
            FailingCommandHandler = new ThrowingMessageHandler<FailingCommand>();

            var services = new ServiceCollection();
            services.AddSingleton<IMessageHandler<TracedCommand>>(CommandHandler);
            services.AddSingleton<IMessageHandler<TracedEvent>>(EventMessageHandler);
            services.AddSingleton<IMessageHandler<FailingCommand>>(FailingCommandHandler);
            services.AddSingleton<IDispatchMessages>(provider => new CommandDispatcher(provider, NullLogger<CommandDispatcher>.Instance));
            services.AddSingleton<IDispatchMessages>(provider => new EventDispatcher(provider, NullLogger<EventDispatcher>.Instance));
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

        public Task DispatchCommand() => Dispatcher.Dispatch(new TracedCommand());

        public Task DispatchEvent() => Dispatcher.Dispatch(new TracedEvent());

        public Task DispatchFailingCommand() => Dispatcher.Dispatch(new FailingCommand());

        public void Dispose() => _serviceProvider.Dispose();
    }
}
