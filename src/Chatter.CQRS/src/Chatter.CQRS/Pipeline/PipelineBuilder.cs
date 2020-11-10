using Chatter.CQRS.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.CQRS.Pipeline
{
    public class PipelineBuilder
    {
        public IServiceCollection Services { get; private set; }

        internal PipelineBuilder(IServiceCollection services)
        {
            Services = services;
        }

        public PipelineBuilder WithBehavior<TCommandBehavior>()
            => WithBehavior(typeof(TCommandBehavior));

        public PipelineBuilder WithBehavior(Type behaviorType)
        {
            Services.AddPipelineBehavior(behaviorType);
            return this;
        }
    }
}
