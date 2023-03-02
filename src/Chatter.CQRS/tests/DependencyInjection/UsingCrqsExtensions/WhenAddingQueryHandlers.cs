using Chatter.CQRS.Context;
using Chatter.CQRS.Queries;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingQueryHandlers : Testing.Core.Context
    {
        [Fact]
        public void MustThrowIfDuplicateQueryHandlers()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeHandler), typeof(FakeHandler)).Creation;
            var sc = new ServiceCollection();
            Assert.ThrowsAny<Exception>(() => sc.AddQueryHandlers(new Assembly[] { assembly }));
        }

        [Fact]
        public void MustRegisterAllQueryHandlers()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddQueryHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeHandler));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IQueryHandler<FakeQuery, string>));
            sd.ImplementationType.Should().Be(typeof(FakeHandler));
        }

        [Fact]
        public void MustReturnSelf()
        {
            var sc = new ServiceCollection();
            var returnValue = sc.AddQueryHandlers(new Assembly[] { });
            returnValue.Should().BeSameAs(sc);
        }

        private class FakeQuery : IQuery<string> { }
        private class FakeHandler : IQueryHandler<FakeQuery, string>
        {
            public Task<string> Handle(FakeQuery query, IQueryHandlerContext context) => throw new NotImplementedException();
        }
    }
}
