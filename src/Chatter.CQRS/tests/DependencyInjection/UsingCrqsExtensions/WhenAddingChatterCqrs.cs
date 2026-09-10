using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingChatterCqrs : Testing.Core.Context
    {
        [Fact]
        public void MustNotRegisterHandlersFromAssembliesThatWereNotSupplied()
        {
            var suppliedAssembly = New.Common().Assembly.WithTypes(typeof(SuppliedCommandHandler)).Creation;
            var services = new ServiceCollection();

            services.AddChatterCqrs(Mock.Of<IConfiguration>(), messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(suppliedAssembly));

            services.Should().NotContain(sd => sd.ImplementationType == typeof(UnsuppliedCommandHandler));
            services.Should().Contain(sd => sd.ServiceType == typeof(IMessageHandler<SuppliedCommand>)
                                            && sd.ImplementationType == typeof(SuppliedCommandHandler));
        }

        [Fact]
        public void MustRegisterHandlersFromLoadedAssembliesWhenNoAssembliesAreSupplied()
        {
            var services = new ServiceCollection();

            services.AddChatterCqrs(Mock.Of<IConfiguration>());

            services.Should().Contain(sd => sd.ServiceType == typeof(IMessageHandler<UnsuppliedCommand>)
                                            && sd.ImplementationType == typeof(UnsuppliedCommandHandler));
        }

        private class SuppliedCommand : ICommand { }
        private class SuppliedCommandHandler : IMessageHandler<SuppliedCommand>
        {
            public Task Handle(SuppliedCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class UnsuppliedCommand : ICommand { }
        private class UnsuppliedCommandHandler : IMessageHandler<UnsuppliedCommand>
        {
            public Task Handle(UnsuppliedCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }
    }
}
