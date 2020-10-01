using Chatter.CQRS.Queries;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Queries.UsingQueryDispatcher
{
    public class WhenDispatchingGenericQuery : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly Mock<IQueryHandler<IQuery<string>, string>> _handler = new Mock<IQueryHandler<IQuery<string>, string>>();
        private readonly LoggerCreator<QueryDispatcher> _logger;
        private readonly IQuery<string> _query;
        private readonly QueryDispatcher _sut;

        public WhenDispatchingGenericQuery()
        {
            _query = It.IsAny<IQuery<string>>();
            _serviceProvider.Setup(p => p.GetService(typeof(IQueryHandler<IQuery<string>, string>)))
                .Returns(_handler.Object);
            _logger = New.Common().Logger<QueryDispatcher>();
            _sut = new QueryDispatcher(_serviceProvider.Object, _logger.Creation);
        }

        [Fact]
        public async Task MustGetQueryHandlerFromGenericQuery()
        {
            await _sut.Query<IQuery<string>, string>(It.IsAny<IQuery<string>>());
            _serviceProvider.Verify(p => p.GetService(typeof(IQueryHandler<IQuery<string>, string>)), Times.Once);
        }

        [Fact]
        public async Task MustInvokeQueryHandlerWhenRegisteredWIthServiceProvider()
        {
            await _sut.Query<IQuery<string>, string>(_query);
            _handler.Verify(h => h.Handle(_query), Times.Once);
        }

        [Fact]
        public async Task MustThrowIfNoQueryHandlerIsRegisteredWIthServiceProvider()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IQueryHandler<IQuery<string>, string>))).Returns(null);
            await FluentActions.Invoking(async () => await _sut.Query<IQuery<string>, string>(_query)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustThrowIfQueryHandlerThrowsException()
        {
            _handler.Setup(h => h.Handle(_query)).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Query<IQuery<string>, string>(_query)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustLogErrorIfExceptionIsCaught()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IQueryHandler<IQuery<string>, string>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Query<IQuery<string>, string>(_query)).Should().ThrowAsync<Exception>();
            _logger.ThatLogsError()
                   .IsCalled(Times.Once());
        }

        [Fact]
        public async Task MustReturnValueFromQueryHandlerIfSuccessful()
        {
            var returnValue = "result";
            _handler.Setup(h => h.Handle(_query)).Returns(() => Task.FromResult(returnValue));
            var result = await _sut.Query<IQuery<string>, string>(_query);
            result.Should().BeAssignableTo<string>();
            result.Should().BeSameAs(returnValue);
            result.Should().Be(returnValue);
        }
    }
}
