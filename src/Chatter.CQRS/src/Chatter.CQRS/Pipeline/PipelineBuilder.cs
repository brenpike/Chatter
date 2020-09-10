using Microsoft.Extensions.DependencyInjection;

namespace Chatter.CQRS.Pipeline
{
    public class PipelineBuilder
    {
        private readonly IServiceCollection _services;

        internal PipelineBuilder(IServiceCollection services)
        {
            _services = services;
        }

        public PipelineBuilder WithStep<TPipelineStep>() where TPipelineStep : class, ICommandBehavior
        {
            _services.AddTransient<ICommandBehavior, TPipelineStep>();
            return this;
        }
    }
}
