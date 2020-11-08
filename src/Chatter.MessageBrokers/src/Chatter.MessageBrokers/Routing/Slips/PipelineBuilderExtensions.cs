using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Routing.Slips;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class PipelineBuilderExtensions
    {
        public static PipelineBuilder WithRoutingSlipBehavior(this PipelineBuilder pipelineBuilder)
        {
            pipelineBuilder.WithBehavior(typeof(RoutingSlipBehavior<>));
            return pipelineBuilder;
        }
    }
}
