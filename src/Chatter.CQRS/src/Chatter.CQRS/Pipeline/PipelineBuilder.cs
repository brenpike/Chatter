using Chatter.CQRS.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Reflection;

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
            if (!behaviorType.GetTypeInfo().ImplementedInterfaces.Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandBehavior<>)))
            {
                throw new ArgumentException($"The supplied type must implement {typeof(ICommandBehavior<>).Name}", nameof(behaviorType));
            }

            if (behaviorType.IsGenericTypeDefinition)
            {
                Services.RegisterBehaviorForAllCommands(behaviorType);
            }
            else
            {
                Services.RegisterBehaviorForCommand(behaviorType);
            }

            return this;
        }
    }
}
