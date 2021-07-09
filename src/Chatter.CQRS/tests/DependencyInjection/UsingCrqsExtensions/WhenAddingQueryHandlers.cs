using Chatter.CQRS.Queries;
using System.Threading.Tasks;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingQueryHandlers
    {
        //[Fact]
        //public void MustAddAllQueryHandlersInExecutingAssemblyToServiceCollection()
        //{
        //    var sc = new ServiceCollection();
        //    sc.AddQueryHandlers();

        //    sc.Should().HaveCount(2);

        //    var qh1 = sc.Where(s => s.ImplementationType == typeof(QueryHandler)).Single();
        //    var qh2 = sc.Where(s => s.ImplementationType == typeof(QueryHandler2)).Single();

        //    qh1.ServiceType.Should().Be(typeof(IQueryHandler<Query, string>));
        //    qh1.Lifetime.Should().Be(ServiceLifetime.Transient);

        //    qh2.ServiceType.Should().Be(typeof(IQueryHandler<Query2, int>));
        //    qh2.Lifetime.Should().Be(ServiceLifetime.Transient);
        //}

        //[Fact]
        //public void MustReturnSelf()
        //{
        //    var sc = new ServiceCollection();
        //    var returnValue = sc.AddQueryHandlers();
        //    returnValue.Should().BeSameAs(sc);
        //}

        //[Fact]
        //public void MustThrowWhenDuplicateQueryHandlers()
        //{
        //    var mockAssembly = new Mock<Assembly>();
        //    var t = new Mock<IQueryHandler<Query2, int>>();
        //    mockAssembly.Setup(a => a.GetTypes()).Returns(new Type[] { t.Object.GetType() });

        //    var sc = new ServiceCollection();
        //    FluentActions.Invoking(() => sc.AddQueryHandlers(mockAssembly.Object.GetTypes())).Should().ThrowExactly<DuplicateTypeRegistrationException>();
        //}

        private class Query : IQuery<string> { }
        private class Query2 : IQuery<int> { }
        private class QueryHandler : IQueryHandler<Query, string>
        {
            public Task<string> Handle(Query query) => Task.FromResult("");
        }
        private class QueryHandler2 : IQueryHandler<Query2, int>
        {
            public Task<int> Handle(Query2 query) => Task.FromResult(1);
        }
    }
}
