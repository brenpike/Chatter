using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Chatter.Testing.Core.Creators.Common
{
    public class LoggerCreator<T> : Creator<ILogger<T>>
    {
        private readonly Mock<ILogger<T>> _loggerMock = new Mock<ILogger<T>>();
        private LogLevel _currentLogLevel;
        private string _currentLogMessage = "";
        private Exception _currentException = null;

        public LoggerCreator(INewContext newContext, ILogger<T> creation = null)
            : base(newContext, creation)
        {
            _currentLogMessage = "";
            _currentException = null;
            ThatLogsTrace();
            Creation = _loggerMock.Object;
        }

        public LoggerCreator<T> ThatLogsTrace()
            => ThatLogsWithLevel(LogLevel.Trace);

        public LoggerCreator<T> ThatLogsDebug()
            => ThatLogsWithLevel(LogLevel.Debug);

        public LoggerCreator<T> ThatLogsInformation()
            => ThatLogsWithLevel(LogLevel.Information);

        public LoggerCreator<T> ThatLogsWarning()
            => ThatLogsWithLevel(LogLevel.Warning);

        public LoggerCreator<T> ThatLogsError()
            => ThatLogsWithLevel(LogLevel.Error);

        public LoggerCreator<T> ThatLogsCritical()
            => ThatLogsWithLevel(LogLevel.Critical);

        public LoggerCreator<T> ThatLogsNone()
            => ThatLogsWithLevel(LogLevel.None);

        public LoggerCreator<T> WithMessage(string message)
        {
            _currentLogMessage = message;
            return this;
        }

        public LoggerCreator<T> WithException(Exception e)
        {
            _currentException = e;
            return this;
        }

        public LoggerCreator<T> IsCalled(Times times)
        {
            _loggerMock.Verify(Log(_currentLogLevel, _currentLogMessage, _currentException), times);
            return this;
        }

        private Expression<Action<ILogger<T>>> Log(LogLevel level, string message = "", Exception exception = null)
        {
            return x => x.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => string.IsNullOrWhiteSpace(message) || MatchesLogValues(o, message)),
                    It.Is<Exception>(e => e == null || e == exception),
                    It.Is<Func<It.IsAnyType, Exception, string>>((o, t) => true)
            );
        }

        private bool MatchesLogValues(object state, string expectedMessage)
        {
            const string messageKeyName = "{OriginalFormat}";
            var loggedValues = (IReadOnlyList<KeyValuePair<string, object>>)state;
            return loggedValues.Any(loggedValue => loggedValue.Key == messageKeyName && loggedValue.Value.ToString() == expectedMessage);
        }

        private LoggerCreator<T> ThatLogsWithLevel(LogLevel logLevel)
        {
            _currentLogLevel = logLevel;
            _loggerMock.Setup(Log(logLevel));
            return this;
        }
    }
}
