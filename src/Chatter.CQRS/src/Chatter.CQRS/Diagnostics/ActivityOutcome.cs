using System;
using System.Diagnostics;

namespace Chatter.CQRS.Diagnostics
{
    /// <summary>
    /// The single place a failed <see cref="Activity"/> is marked, so every Chatter call site records failure
    /// identically and none invents its own spelling or status.
    /// </summary>
    public static class ActivityOutcome
    {
        /// <summary>
        /// Marks <paramref name="activity"/> as failed: sets <see cref="ActivityStatusCode.Error"/> with the
        /// exception message and stamps <see cref="ChatterTelemetryTags.ErrorType"/>. The full exception event is
        /// added only when <see cref="Activity.IsAllDataRequested"/> is <c>true</c>, so a recording-only span never
        /// pays to materialise exception detail.
        /// </summary>
        /// <param name="activity">The span to mark, or <c>null</c> when no span was started.</param>
        /// <param name="exception">The exception that ended the operation.</param>
        /// <remarks>Passing a <c>null</c> <paramref name="activity"/> or <paramref name="exception"/> is a no-op:
        /// diagnostics run inside catch blocks and must never displace the failure being reported.</remarks>
        public static void RecordFailure(Activity activity, Exception exception)
        {
            if (activity is null || exception is null)
            {
                return;
            }

            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.SetTag(ChatterTelemetryTags.ErrorType, ResolveErrorType(exception));

            if (!activity.IsAllDataRequested)
            {
                return;
            }

            AddExceptionEvent(activity, exception);
        }

        /// <summary>
        /// The value Chatter emits for <see cref="ChatterTelemetryTags.ErrorType"/>: the fully qualified exception
        /// type name, as the OpenTelemetry <c>error.type</c> convention prescribes for exception-shaped errors.
        /// </summary>
        /// <param name="exception">The exception that ended the operation.</param>
        /// <returns>The fully qualified exception type name, or <c>null</c> when <paramref name="exception"/> is <c>null</c>.</returns>
        public static string ResolveErrorType(Exception exception) => exception?.GetType().FullName;

#if NET9_0_OR_GREATER
        private static void AddExceptionEvent(Activity activity, Exception exception) => activity.AddException(exception);
#else
        private static void AddExceptionEvent(Activity activity, Exception exception)
        {
            var exceptionTags = new ActivityTagsCollection
            {
                { ChatterTelemetryTags.ExceptionType, exception.GetType().FullName },
                { ChatterTelemetryTags.ExceptionMessage, exception.Message },
                { ChatterTelemetryTags.ExceptionStackTrace, exception.ToString() }
            };

            activity.AddEvent(new ActivityEvent(ChatterTelemetryTags.ExceptionEventName, tags: exceptionTags));
        }
#endif
    }
}
