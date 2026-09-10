using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingInMemoryBrokeredMessageOutbox
{
    public class WhenExecutingUnitOfWork : Testing.Core.Context
    {
        private readonly RecordingLoggerCreator<InMemoryBrokeredMessageOutbox> _logger;
        private readonly ReliabilityOptions _reliabilityOptions = new ReliabilityOptions();
        private readonly InMemoryBrokeredMessageOutbox _outbox;
        private readonly IUnitOfWork _sut;

        public WhenExecutingUnitOfWork()
        {
            _logger = New.Common().RecordingLogger<InMemoryBrokeredMessageOutbox>();
            _outbox = new InMemoryBrokeredMessageOutbox(_logger.Creation, _reliabilityOptions);
            _sut = _outbox;
        }

        [Fact]
        public void MustCastResolvedOutboxToUnitOfWorkWithoutThrowing()
        {
            // INVARIANT: OutboxProcessor.Process obtains the Unit of Work by casting the single
            // resolved IBrokeredMessageOutbox at the consumption site (Reliability-Store Facet
            // Resolution). The default in-memory store is what DI registers, so that cast must
            // succeed on it or the drain never reaches UpdateProcessedDate or the dispatch.
            IBrokeredMessageOutbox resolved = _outbox;
            FluentActions.Invoking(() => (IUnitOfWork)resolved).Should().NotThrow();
        }

        [Fact]
        public async Task MustInvokeTheSuppliedOperation()
        {
            var wasInvoked = false;

            await _sut.ExecuteAsync(ct =>
            {
                wasInvoked = true;
                return Task.CompletedTask;
            }, null);

            wasInvoked.Should().BeTrue();
        }

        [Fact]
        public async Task MustInvokeTheSuppliedOperationWithTheSuppliedCancellationToken()
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var observedToken = CancellationToken.None;

            await _sut.ExecuteAsync(ct =>
            {
                observedToken = ct;
                return Task.CompletedTask;
            }, null, cancellationTokenSource.Token);

            observedToken.Should().Be(cancellationTokenSource.Token);
        }

        [Fact]
        public async Task MustPropagateTheOperationExceptionUnchanged()
        {
            var thrown = new InvalidOperationException("operation failed");

            var caught = await FluentActions.Invoking(async () => await _sut.ExecuteAsync(ct => Task.FromException(thrown), null))
                .Should().ThrowAsync<InvalidOperationException>();

            caught.Which.Should().BeSameAs(thrown);
        }

        [Fact]
        public void MustReportNoActiveTransaction()
        {
            // INVARIANT: the in-memory store is a NON-TRANSACTIONAL pass-through - it opens no
            // transaction, so it never claims one.
            _sut.HasActiveTransaction.Should().BeFalse();
        }

        [Fact]
        public void MustReportNoCurrentTransaction()
        {
            _sut.CurrentTransaction.Should().BeNull();
        }
    }
}
