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

        public PipelineBuilder WithStep<TPipelineStep>() where TPipelineStep : class, IMessageHandlerPipelineStep
        {
            _services.AddTransient<IMessageHandlerPipelineStep, TPipelineStep>();
            return this;
        }
    }
}
