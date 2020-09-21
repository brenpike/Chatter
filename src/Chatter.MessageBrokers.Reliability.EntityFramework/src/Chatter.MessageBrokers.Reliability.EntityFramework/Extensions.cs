using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        public static PipelineBuilder WithUnitOfWorkBehavior<TContext>(this PipelineBuilder pipelineBuilder, IServiceCollection services) where TContext : DbContext
        {
            services.AddScoped<IUnitOfWork, UnitOfWork<TContext>>();
            pipelineBuilder.WithBehavior(typeof(UnitOfWorkBehavior<>));

            return pipelineBuilder;
        }

        public static PipelineBuilder WithInboxBehavior<TContext>(this PipelineBuilder pipelineBuilder, IServiceCollection services) where TContext : DbContext
        {
            pipelineBuilder.WithUnitOfWorkBehavior<TContext>(services);
            services.AddScoped<ITransactionalBrokeredMessageOutbox, TransactionalOutbox<TContext>>();
            pipelineBuilder.WithBehavior(typeof(InboxBehavior<>));

            return pipelineBuilder;
        }
    }
}
