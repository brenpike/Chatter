using Microsoft.Extensions.Logging;
using System;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics
{
    /// <summary>
    /// The module's ONLY handle on the application-supplied logging sink: an optional <see cref="ILogger"/> that can
    /// never escape into the caller's control flow. Every relay type holds THIS instead of an <see cref="ILogger"/>,
    /// so an unguarded optional-sink log call is not reachable from the relay at all.
    /// </summary>
    /// <remarks>
    /// WHY A TYPE AND NOT A TRY/CATCH PER CALL SITE. A logging provider is an OPTIONAL, application-supplied sink, and
    /// this module invokes it on paths whose whole purpose is to preserve or emit something LOAD-BEARING: the
    /// start-failure cleanup that must rethrow the ORIGINAL start failure, and the #361 always-on change-feed-fault
    /// report that is the only channel a meter-less application has. A faulty sink that throws out of one of those
    /// calls REPLACES the essential thing with the optional thing's failure. Guarding each call site individually
    /// completes the known set and leaves the next call site to be found by the next reviewer; holding the sink
    /// through this type instead means the relay owns no raw <see cref="ILogger"/> field to misuse, so the unguarded
    /// form is unrepresentable rather than merely absent.
    /// INVARIANT: no method here ever throws. A sink fault is swallowed — it is never re-reported through the sink
    /// that just faulted, which could only fault again. Observability may never break delivery, and may never decide
    /// which exception a caller observes.
    /// INVARIANT: the sink is OPTIONAL. A null logger is a silent no-op, and the null check happens BEFORE the message
    /// template's arguments are packed, so an application that wired no logger pays one reference comparison and
    /// allocates nothing (the same off-cost the diagnostics guards keep, ADR-0010 R1).
    /// INVARIANT: logging goes through the STRUCTURED message-template overloads only — never string interpolation,
    /// which would render the message before the level is checked. The arity-specific overloads exist for that reason
    /// AND to keep the params-array off the null-logger path.
    /// This type changes NOTHING about WHAT is logged: same level, same template, same arguments, same exception.
    /// </remarks>
    internal readonly struct GuardedRelayLog
    {
        private readonly ILogger _logger;

        internal GuardedRelayLog(ILogger logger) => _logger = logger;

        /// <summary>Reports <paramref name="exception"/> at <see cref="LogLevel.Error"/> under a constant template.</summary>
        internal void Error(Exception exception, string messageTemplate)
        {
            ILogger logger = _logger;
            if (logger is null)
            {
                return;
            }

            try
            {
                logger.LogError(exception, messageTemplate);
            }
            catch (Exception)
            {
                // Swallowed, and deliberately not re-reported: the sink that just faulted is the only one there is.
            }
        }

        /// <summary>Reports <paramref name="exception"/> at <see cref="LogLevel.Error"/> under a one-argument template.</summary>
        internal void Error<T0>(Exception exception, string messageTemplate, T0 arg0)
        {
            ILogger logger = _logger;
            if (logger is null)
            {
                return;
            }

            try
            {
                logger.LogError(exception, messageTemplate, arg0);
            }
            catch (Exception)
            {
                // Swallowed, and deliberately not re-reported: the sink that just faulted is the only one there is.
            }
        }

        /// <summary>Reports <paramref name="exception"/> at <see cref="LogLevel.Error"/> under a two-argument template.</summary>
        internal void Error<T0, T1>(Exception exception, string messageTemplate, T0 arg0, T1 arg1)
        {
            ILogger logger = _logger;
            if (logger is null)
            {
                return;
            }

            try
            {
                logger.LogError(exception, messageTemplate, arg0, arg1);
            }
            catch (Exception)
            {
                // Swallowed, and deliberately not re-reported: the sink that just faulted is the only one there is.
            }
        }

        /// <summary>
        /// Reports at <see cref="LogLevel.Information"/> under a one-argument template, for a deliberate decision the
        /// relay took rather than a fault it suffered.
        /// </summary>
        internal void Information<T0>(string messageTemplate, T0 arg0)
        {
            ILogger logger = _logger;
            if (logger is null)
            {
                return;
            }

            try
            {
                logger.LogInformation(messageTemplate, arg0);
            }
            catch (Exception)
            {
                // Swallowed, and deliberately not re-reported: the sink that just faulted is the only one there is.
            }
        }
    }
}
