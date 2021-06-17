using Chatter.CQRS.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.CQRS.Pipeline
{
    public class CommandPipelineBuilder
    {
        public IServiceCollection Services { get; private set; }

        internal CommandPipelineBuilder(IServiceCollection services)
            => Services = services ?? throw new ArgumentNullException(nameof(services));

        public CommandPipelineBuilder WithBehavior<TCommandBehavior>()
            => WithBehavior(typeof(TCommandBehavior));

        public CommandPipelineBuilder WithBehavior(Type behaviorType)
        {
            Services.AddPipelineBehavior(behaviorType);
            return this;
        }
    }
}
