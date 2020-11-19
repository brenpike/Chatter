using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.MessageBrokers.Configuration
{
    public static partial class MessageBrokerOptionsBuilderExtensions
    {
        public static MessageBrokerOptionsBuilder UseGuidMessageIdGenerator(this MessageBrokerOptionsBuilder builder)
        {
            builder.Services.Replace<IMessageIdGenerator, GuidIdGenerator>(ServiceLifetime.Scoped);
            return builder;
        }

        public static MessageBrokerOptionsBuilder UseCombGuidMessageIdGenerator(this MessageBrokerOptionsBuilder builder)
        {
            builder.Services.Replace<IMessageIdGenerator, CombGuidIdGenerator>(ServiceLifetime.Scoped);
            return builder;
        }

        public static MessageBrokerOptionsBuilder UseHashedBodyGuidMessageIdGenerator(this MessageBrokerOptionsBuilder builder)
        {
            builder.Services.Replace<IMessageIdGenerator, HashedBodyGuidGenerator>(ServiceLifetime.Scoped);
            return builder;
        }
    }
}
