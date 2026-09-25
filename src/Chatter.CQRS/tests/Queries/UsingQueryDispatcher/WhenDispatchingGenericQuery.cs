using Chatter.CQRS.Context;
using Chatter.CQRS.Queries;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Queries.UsingQueryDispatcher
{
    public class WhenDispatchingGenericQuery : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly Mock<IQueryHandler<IQuery<string>, string>> _handler = new Mock<IQueryHandler<IQuery<string>, string>>();
		private readonly Mock<IQueryHandlerContext> _context = new Mock<IQueryHandlerContext>();
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
            await _sut.Query<IQuery<string>, string>(_query, _context.Object);
            _handler.Verify(h => h.Handle(_query, _context.Object), Times.Once);
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
            _handler.Setup(h => h.Handle(_query, _context.Object)).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Query<IQuery<string>, string>(_query, _context.Object)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustLogErrorIfExceptionIsCaught()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IQueryHandler<IQuery<string>, string>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Query<IQuery<string>, string>(_query)).Should().ThrowAsync<Exception>();
            _logger.VerifyWasCalled(LogLevel.Error, times: Times.Once());
        }

        [Fact]
        public async Task MustLogCaughtExceptionAsTheLogRecordExceptionWithoutStackTraceInMessage()
        {
            var fault = new Exception("query dispatch failed");
            var expectedMessage = $"Error dispatching query of type '{typeof(IQuery<string>).Name}'";
            _serviceProvider.Setup(p => p.GetService(typeof(IQueryHandler<IQuery<string>, string>))).Throws(fault);
            await FluentActions.Invoking(async () => await _sut.Query<IQuery<string>, string>(_query)).Should().ThrowAsync<Exception>();
            _logger.VerifyWasCalled(LogLevel.Error, expectedMessage, fault, Times.Once());
            _logger.LoggedMessages.Should().ContainSingle();
            _logger.LoggedMessages[0].message.Should().Be(expectedMessage);
        }

        [Fact]
        public async Task MustReturnValueFromQueryHandlerIfSuccessful()
        {
            var returnValue = "result";
            _handler.Setup(h => h.Handle(_query, _context.Object)).Returns(() => Task.FromResult(returnValue));
            var result = await _sut.Query<IQuery<string>, string>(_query, _context.Object);
            result.Should().BeAssignableTo<string>();
            result.Should().BeSameAs(returnValue);
            result.Should().Be(returnValue);
        }

        [Fact]
        public async Task MustRethrowACallerRequestedCancellationAndLogItOnceAtDebugInsteadOfError()
        {
            var cancellation = ArrangeQueryHandlerThatFaultsAfterAnAwait(new OperationCanceledException("The caller cancelled the query."));

            var thrown = await FluentActions.Invoking(async () => await _sut.Query<IQuery<string>, string>(_query, ContextCancelledByTheCaller())).Should().ThrowAsync<OperationCanceledException>();

            thrown.Which.Should().BeSameAs(cancellation);
            _logger.VerifyWasCalled(LogLevel.Debug, $"Dispatch of query '{typeof(IQuery<string>).Name}' was cancelled by the caller.", cancellation, Times.Once());
            _logger.VerifyWasCalled(LogLevel.Debug, times: Times.Once());
            _logger.VerifyWasCalled(LogLevel.Error, times: Times.Never());
        }

        [Fact]
        public async Task MustLogErrorNotDebugWhenTheCancellationWasNotRequestedByTheCaller()
        {
            var cancellation = ArrangeQueryHandlerThatFaultsAfterAnAwait(new OperationCanceledException("A spontaneous timeout cancelled the query handler."));

            await FluentActions.Invoking(async () => await _sut.Query<IQuery<string>, string>(_query, new QueryHandlerContext(CancellationToken.None))).Should().ThrowAsync<OperationCanceledException>();

            _logger.VerifyWasCalled(LogLevel.Error, $"Error dispatching query of type '{typeof(IQuery<string>).Name}'", cancellation, Times.Once());
            _logger.VerifyWasCalled(LogLevel.Error, times: Times.Once());
            _logger.VerifyWasCalled(LogLevel.Debug, times: Times.Never());
        }

        private TException ArrangeQueryHandlerThatFaultsAfterAnAwait<TException>(TException failure) where TException : Exception
        {
            _handler.Setup(h => h.Handle(It.IsAny<IQuery<string>>(), It.IsAny<IQueryHandlerContext>()))
                .Returns(() => FaultAfterAnAwait(failure));
            return failure;
        }

        private static QueryHandlerContext ContextCancelledByTheCaller()
        {
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            return new QueryHandlerContext(cancellationSource.Token);
        }

        private static async Task<string> FaultAfterAnAwait(Exception failure)
        {
            await Task.Yield();
            throw failure;
        }
    }
}
