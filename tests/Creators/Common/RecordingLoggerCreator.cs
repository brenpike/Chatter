using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.Testing.Core.Creators.Common
{
    /// <summary>
    /// A hand-written <see cref="ILogger{T}"/> recorder that captures log calls without a Castle
    /// dynamic proxy. Unlike <see cref="LoggerCreator{T}"/>, this works when <typeparamref name="T"/>
    /// is an internal type, because Moq cannot proxy a generic logger over an internal type unless the
    /// production assembly grants InternalsVisibleTo to DynamicProxyGenAssembly2.
    /// </summary>
    public class RecordingLoggerCreator<T> : Creator<ILogger<T>>
    {
        public List<(LogLevel level, string message)> LoggedMessages { get; } = new List<(LogLevel level, string message)>();

        public RecordingLoggerCreator(INewContext newContext, ILogger<T> creation = null)
            : base(newContext, creation)
        {
            Creation = new RecordingLogger(LoggedMessages);
        }

        public int CountOf(LogLevel level, string expectedMessage = null)
            => LoggedMessages.Count(m =>
                m.level == level && (expectedMessage == null || m.message == expectedMessage));

        public RecordingLoggerCreator<T> VerifyWasCalled(LogLevel level, string expectedMessage, int expectedCount)
        {
            var matches = CountOf(level, expectedMessage);
            if (matches != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Expected a log at level {level} with message '{expectedMessage ?? "<any>"}' {expectedCount} time(s) but observed {matches}.");
            }

            return this;
        }

        private sealed class RecordingLogger : ILogger<T>
        {
            private readonly List<(LogLevel level, string message)> _messages;
            public RecordingLogger(List<(LogLevel level, string message)> messages) => _messages = messages;

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
                => _messages.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
