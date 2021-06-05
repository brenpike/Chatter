using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability.Outbox;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    public static class CommandPipelineBuilderExtensions
    {
        public static CommandPipelineBuilder WithTransactionScopeSupressionBehavior(this CommandPipelineBuilder pipelineBuilder)
        {
            pipelineBuilder.WithBehavior(typeof(TransactionScopeSupressionBehavior<>));

            pipelineBuilder.Services.InsertServiceBefore(typeof(TransactionScopeSupressionBehavior<>), typeof(OutboxProcessingBehavior<>));

            return pipelineBuilder;
        }
    }
}
