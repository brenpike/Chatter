using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingChatterCqrs
    {
        private ServiceCollection _serviceCollection;

        public WhenAddingChatterCqrs()
        {

        }

        [Fact]
        public void MustNotAddTypeToCommandBehaviorPipelineIfTypeIsNotICommandBehavior()
        {
        }
    }
}
