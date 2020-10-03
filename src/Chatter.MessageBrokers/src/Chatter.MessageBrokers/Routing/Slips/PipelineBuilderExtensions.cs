using Chatter.CQRS.Pipeline;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class PipelineBuilderExtensions
    {
        public static PipelineBuilder WithRoutingSlipRoutingBehavior(this PipelineBuilder pipelineBuilder)
        {
            pipelineBuilder.WithBehavior(typeof(RoutingSlipBehavior<>));
            return pipelineBuilder;
        }
    }
}
