using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Routing.Slips;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class CommandPipelineBuilderExtensions
    {
        public static CommandPipelineBuilder WithRoutingSlipBehavior(this CommandPipelineBuilder pipelineBuilder)
        {
            pipelineBuilder.WithBehavior(typeof(RoutingSlipBehavior<>));
            return pipelineBuilder;
        }
    }
}
