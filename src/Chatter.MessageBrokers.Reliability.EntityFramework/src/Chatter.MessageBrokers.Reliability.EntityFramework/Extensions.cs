using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        public static CommandPipelineBuilder WithUnitOfWorkBehavior<TContext>(this CommandPipelineBuilder pipelineBuilder) 
            where TContext : DbContext
        {
            pipelineBuilder.Services.Replace<IUnitOfWork, UnitOfWork<TContext>>(ServiceLifetime.Scoped);
            pipelineBuilder.WithBehavior(typeof(UnitOfWorkBehavior<>));

            return pipelineBuilder;
        }

        public static CommandPipelineBuilder WithInboxBehavior<TContext>(this CommandPipelineBuilder pipelineBuilder) 
            where TContext : DbContext
        {
            pipelineBuilder.WithUnitOfWorkBehavior<TContext>();
            pipelineBuilder.Services.Replace<IBrokeredMessageInbox, BrokeredMessageInbox<TContext>>(ServiceLifetime.Scoped);
            pipelineBuilder.WithBehavior(typeof(InboxBehavior<>));

            return pipelineBuilder;
        }

        public static CommandPipelineBuilder WithOutboxProcessingBehavior<TContext>(this CommandPipelineBuilder pipelineBuilder)
            where TContext : DbContext
        {
            pipelineBuilder.WithBehavior(typeof(OutboxProcessingBehavior<>));
            pipelineBuilder.Services.Replace<IBrokeredMessageOutbox, BrokeredMessageOutbox<TContext>>(ServiceLifetime.Scoped);
            pipelineBuilder.Services.Replace<IRouteBrokeredMessages, OutboxBrokeredMessageRouter>(ServiceLifetime.Scoped);
            pipelineBuilder.WithUnitOfWorkBehavior<TContext>();

            return pipelineBuilder;
        }
    }
}
