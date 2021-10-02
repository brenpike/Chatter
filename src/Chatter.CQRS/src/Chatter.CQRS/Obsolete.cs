using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Chatter.CQRS
{
    //other obsolete functions go here
}

namespace Microsoft.Extensions.DependencyInjection
{
    [ExcludeFromCodeCoverage]
    public static class ObsoleteCqrsExtensions
    {
        [Obsolete("This method will be deprecated in version 1.0.0. Use IServiceCollection.AddChatterCqrs(IConfiguration, Action<PipelineBuilder>, params Type[]) instead.", false)]
        public static IChatterBuilder AddCommandPipeline(this IChatterBuilder chatterBuilder, Action<CommandPipelineBuilder> pipelineBulder)
            => CqrsExtensions.AddCommandPipeline(chatterBuilder, pipelineBulder);
    }
}
