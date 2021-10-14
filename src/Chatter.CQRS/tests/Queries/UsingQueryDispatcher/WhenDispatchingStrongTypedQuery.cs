using Chatter.CQRS.Queries;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Queries.UsingQueryDispatcher
{
    public class WhenDispatchingStrongTypedQuery : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly Mock<IQueryHandler<TestQuery, string>> _handler = new Mock<IQueryHandler<TestQuery, string>>();
        private readonly LoggerCreator<QueryDispatcher> _logger;
        private readonly QueryDispatcher _sut;
        private readonly TestQuery _query;

        public class TestQuery : IQuery<string> { }

        public WhenDispatchingStrongTypedQuery()
        {
            _query = new TestQuery();
            _serviceProvider.Setup(p => p.GetService(typeof(IQueryHandler<TestQuery, string>)))
                .Returns(_handler.Object);
            _logger = New.Common().Logger<QueryDispatcher>();
            _sut = new QueryDispatcher(_serviceProvider.Object, _logger.Creation);
        }

        [Fact]
        public async Task MustGetQueryHandlerFromStronglyTypedQuery()
        {
            await _sut.Query(_query);
            _serviceProvider.Verify(p => p.GetService(typeof(IQueryHandler<TestQuery, string>)), Times.Once);
        }

        [Fact]
        public async Task MustInvokeQueryHandlerWhenRegisteredWIthServiceProvider()
        {
            await _sut.Query(_query);
            _handler.Verify(h => h.Handle(It.IsAny<TestQuery>()), Times.Once);
        }

        [Fact]
        public async Task MustThrowIfNoQueryHandlerIsRegisteredWIthServiceProvider()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IQueryHandler<TestQuery, string>))).Returns(null);
            await FluentActions.Invoking(async () => await _sut.Query(_query)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustThrowIfQueryHandlerThrowsException()
        {
            _handler.Setup(h => h.Handle(It.IsAny<TestQuery>())).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Query(_query)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustLogErrorIfExceptionIsCaught()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IQueryHandler<TestQuery, string>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Query(_query)).Should().ThrowAsync<Exception>();
            _logger.VerifyWasCalled(LogLevel.Error, times: Times.Once());
        }

        [Fact]
        public async Task MustReturnValueFromQueryHandlerIfSuccessful()
        {
            _handler.Setup(h => h.Handle(It.IsAny<TestQuery>())).ReturnsAsync("result");
            var result = await _sut.Query(_query);
            result.Should().BeAssignableTo<string>();
            result.Should().NotBeNullOrEmpty();
            result.Should().Be("result");
        }
    }
}
