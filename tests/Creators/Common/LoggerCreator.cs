using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;

namespace Chatter.Testing.Core.Creators.Common
{
    public class LoggerCreator<T> : Creator<ILogger<T>>
    {
        private readonly Mock<ILogger<T>> _loggerMock;
        public List<(LogLevel level, string message)> LoggedMessages { get; } = new List<(LogLevel level, string message)>();

        public LoggerCreator(INewContext newContext, ILogger<T> creation = null)
            : base(newContext, creation)
        {
            _loggerMock = new Mock<ILogger<T>>();
            _loggerMock.Setup(x => x.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()))
                .Callback(new InvocationAction(invocation =>
                {
                    var logLevel = (LogLevel)invocation.Arguments[0];
                    var eventId = (EventId)invocation.Arguments[1];
                    var state = invocation.Arguments[2];
                    var exception = (Exception)invocation.Arguments[3];
                    var formatter = invocation.Arguments[4];

                    var invokeMethod = formatter.GetType().GetMethod("Invoke");
                    var logMessage = (string)invokeMethod?.Invoke(formatter, new[] { state, exception });

                    LoggedMessages.Add((logLevel, logMessage));
                }));
            Creation = _loggerMock.Object;
        }

        public LoggerCreator<T> VerifyWasCalled(LogLevel level, string expectedMessage = null, Times times = default)
        {
            Func<object, Type, bool> state = (v, t) => expectedMessage == null || v.ToString().CompareTo(expectedMessage) == 0;

            _loggerMock.Verify(
                x => x.Log(
                    It.Is<LogLevel>(l => l == level),
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => state(v, t)),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)), times);

            return this;
        }
    }
}
