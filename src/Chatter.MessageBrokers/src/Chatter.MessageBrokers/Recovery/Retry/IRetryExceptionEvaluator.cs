using System;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    public interface IRetryExceptionEvaluator
    {
        bool ShouldRetry(Exception e);
    }
}
