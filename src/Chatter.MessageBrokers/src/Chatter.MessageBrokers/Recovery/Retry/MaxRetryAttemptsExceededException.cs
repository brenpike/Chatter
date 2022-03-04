using System;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    public class MaxRetryAttemptsExceededException : Exception
    {
        private const string MaxRetryAttemptsExceededMessage = "The maximum number of retries was exceeded.";

        public MaxRetryAttemptsExceededException(Exception inner, int attempts)
            : base(MaxRetryAttemptsExceededMessage, inner)
        {
            Attempts = attempts;
        }

        public int Attempts { get; }
    }
}
