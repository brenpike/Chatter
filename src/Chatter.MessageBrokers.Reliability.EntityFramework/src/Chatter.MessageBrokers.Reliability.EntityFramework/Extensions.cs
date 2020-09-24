using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        public static PipelineBuilder WithUnitOfWorkBehavior<TContext>(this PipelineBuilder pipelineBuilder, IServiceCollection services) 
            where TContext : DbContext
        {
            services.Replace<IBrokeredMessageOutbox, BrokeredMessageOutbox<TContext>>(ServiceLifetime.Scoped);
            services.Replace<IUnitOfWork, UnitOfWork<TContext>>(ServiceLifetime.Scoped);
            pipelineBuilder.WithBehavior(typeof(UnitOfWorkBehavior<>));

            return pipelineBuilder;
        }

        public static PipelineBuilder WithInboxBehavior<TContext>(this PipelineBuilder pipelineBuilder, IServiceCollection services) 
            where TContext : DbContext
        {
            pipelineBuilder.WithUnitOfWorkBehavior<TContext>(services);
            services.Replace<IBrokeredMessageInbox, BrokeredMessageInbox<TContext>>(ServiceLifetime.Scoped);
            pipelineBuilder.WithBehavior(typeof(InboxBehavior<>));

            return pipelineBuilder;
        }

        public static PipelineBuilder WithOutboxProcessingBehavior<TContext>(this PipelineBuilder pipelineBuilder, IServiceCollection services)
            where TContext : DbContext
        {
            pipelineBuilder.WithBehavior(typeof(OutboxProcessingBehavior<>));
            pipelineBuilder.WithUnitOfWorkBehavior<TContext>(services);

            return pipelineBuilder;
        }
    }
}
