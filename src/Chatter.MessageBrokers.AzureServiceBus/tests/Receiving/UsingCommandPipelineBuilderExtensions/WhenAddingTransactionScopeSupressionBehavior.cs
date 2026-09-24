using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingCommandPipelineBuilderExtensions
{
    public class WhenAddingTransactionScopeSupressionBehavior : Testing.Core.Context
    {
        [Fact]
        public void MustRegisterTheInternalTransactionScopeSupressionBehavior()
        {
            var services = new ServiceCollection();
            var emptyAssembly = New.Common().Assembly.WithTypes().Creation;

            services.AddChatterCqrs(Mock.Of<IConfiguration>(), p => p.WithTransactionScopeSupressionBehavior(), b => b.WithExplicitAssemblies(emptyAssembly));

            typeof(TransactionScopeSupressionBehavior<>).IsPublic.Should().BeFalse();
            services.Should().ContainSingle(sd => sd.ServiceType == typeof(ICommandBehavior<>)
                                                  && sd.ImplementationType == typeof(TransactionScopeSupressionBehavior<>));
        }
    }
}
