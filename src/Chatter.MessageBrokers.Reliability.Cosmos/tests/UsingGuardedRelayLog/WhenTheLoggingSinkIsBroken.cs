using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingGuardedRelayLog
{
    /// <summary>
    /// The module's only handle on the application-supplied logging sink. Every relay type holds THIS instead of an
    /// <see cref="ILogger"/>, which is what makes an unguarded optional-sink log call unrepresentable in the relay
    /// rather than merely absent from the call sites written so far: a logging provider that throws can never escape
    /// into the control flow of a path whose purpose is to preserve or emit something load-bearing.
    /// </summary>
    public class WhenTheLoggingSinkIsBroken
    {
        private static readonly Exception _fault = new InvalidOperationException("the reported fault");

        /// <summary>A broken provider may not escape the constant-template overload.</summary>
        [Fact]
        public void MustSwallowASinkThrowOnTheConstantTemplateOverload()
        {
            var log = new GuardedRelayLog(new ThrowingLogger());

            Action reporting = () => log.Error(_fault, "the relay could not stop a change-feed processor");

            reporting.Should().NotThrow("a faulty optional sink may never replace the failure the caller must observe");
        }

        /// <summary>The same guarantee on the one-argument structured overload.</summary>
        [Fact]
        public void MustSwallowASinkThrowOnTheOneArgumentTemplateOverload()
        {
            var log = new GuardedRelayLog(new ThrowingLogger());

            Action reporting = () => log.Error(_fault, "the change feed faulted on lease {LeaseToken}", "lease-7");

            reporting.Should().NotThrow("a faulty optional sink may never replace the failure the caller must observe");
        }

        /// <summary>What is logged is UNCHANGED: same level, same rendered template, same exception.</summary>
        [Fact]
        public void MustReportThroughTheStructuredTemplateAtError()
        {
            var logger = new RecordingLogger();
            var log = new GuardedRelayLog(logger);

            log.Error(_fault, "the change feed faulted on lease {LeaseToken}", "lease-7");

            var entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Error);
            entry.Message.Should().Contain("lease-7");
            entry.Exception.Should().BeSameAs(_fault);
        }

        /// <summary>The sink is OPTIONAL: a host that resolved no logger gets a silent no-op, not a null dereference.</summary>
        [Fact]
        public void MustBeASilentNoOpWhenNoLoggerWasSupplied()
        {
            var log = new GuardedRelayLog(logger: null);

            Action reporting = () =>
            {
                log.Error(_fault, "the relay could not stop a change-feed processor");
                log.Error(_fault, "the change feed faulted on lease {LeaseToken}", "lease-7");
            };

            reporting.Should().NotThrow("observability may never be a prerequisite of the path it reports on");
        }

        /// <summary>An <see cref="ILogger"/> recorder that captures the rendered message of each log call.</summary>
        private sealed class RecordingLogger : ILogger
        {
            public List<(LogLevel Level, string Message, Exception Exception)> Entries { get; } = new List<(LogLevel, string, Exception)>();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception), exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();

                public void Dispose()
                {
                }
            }
        }

        /// <summary>An <see cref="ILogger"/> whose sink is broken, as a misconfigured application's can be.</summary>
        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) => throw new InvalidOperationException("the logging sink is broken");

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => throw new InvalidOperationException("the logging sink is broken");
        }
    }
}
