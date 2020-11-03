using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability.Outbox;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    public static class PipelineBuilderExtensions
    {
        public static PipelineBuilder WithTransactionScopeSupressionBehavior(this PipelineBuilder pipelineBuilder)
        {
            pipelineBuilder.WithBehavior(typeof(TransactionScopeSupressionBehavior<>));

            pipelineBuilder.Services.InsertServiceBefore(typeof(TransactionScopeSupressionBehavior<>), typeof(OutboxProcessingBehavior<>));

            return pipelineBuilder;
        }
    }
}
