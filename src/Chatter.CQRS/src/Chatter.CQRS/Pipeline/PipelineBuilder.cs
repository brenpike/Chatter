using Microsoft.Extensions.DependencyInjection;
using Scrutor;
using System;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.Pipeline
{
    public class PipelineBuilder
    {
        private readonly IServiceCollection _services;

        internal PipelineBuilder(IServiceCollection services)
        {
            _services = services;
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
                _services.Scan(s =>
                        s.FromAssemblies(behaviorType.GetTypeInfo().Assembly)
                            .AddClasses(c => c.AssignableTo(behaviorType))
                            .UsingRegistrationStrategy(RegistrationStrategy.Append)
                            .AsImplementedInterfaces()
                            .WithTransientLifetime());
            }
            else
            {
                var behaviorCommandType = behaviorType.GetGenericArguments().SingleOrDefault();
                var closedCommandBehaviorInterface = typeof(ICommandBehavior<>).MakeGenericType(behaviorCommandType);
                _services.AddTransient(closedCommandBehaviorInterface, behaviorType);
            }

            return this;
        }
    }
}
