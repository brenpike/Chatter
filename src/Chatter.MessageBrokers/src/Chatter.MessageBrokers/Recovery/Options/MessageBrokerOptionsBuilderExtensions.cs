﻿using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Recovery;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.MessageBrokers.Configuration
{
    public static class MessageBrokerOptionsBuilderExtensions
    {
        public static MessageBrokerOptionsBuilder UseNoDelayRecovery(this MessageBrokerOptionsBuilder builder)
        {
            builder.Services.Replace<IDelayedRecovery, NoDelayRecovery>(ServiceLifetime.Scoped);
            return builder;
        }

        public static MessageBrokerOptionsBuilder UseExponentialDelayRecovery(this MessageBrokerOptionsBuilder builder)
        {
            builder.Services.Replace<IDelayedRecovery, ExponentialDelayRecovery>(ServiceLifetime.Scoped);
            return builder;
        }

        public static MessageBrokerOptionsBuilder UseConstantDelayRecovery(this MessageBrokerOptionsBuilder builder)
        {
            builder.Services.Replace<IDelayedRecovery, ConstantDelayRecovery>(ServiceLifetime.Scoped);
            return builder;
        }

        public static MessageBrokerOptionsBuilder UseRouteToErrorQueueRecoveryAction(this MessageBrokerOptionsBuilder builder)
        {
            builder.Services.Replace<IRecoveryAction, FailureDispatcher>(ServiceLifetime.Scoped);
            return builder;
        }
    }
}
