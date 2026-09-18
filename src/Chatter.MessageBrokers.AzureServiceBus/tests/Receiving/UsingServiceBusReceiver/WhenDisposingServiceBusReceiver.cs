using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
// Disambiguate the local ServiceBusReceiver (system under test) from the SDK type of the same name
// pulled in by `using Azure.Messaging.ServiceBus;` (CS0104).
using ServiceBusReceiver = Chatter.MessageBrokers.AzureServiceBus.Receiving.ServiceBusReceiver;
using ServiceBusClient = Azure.Messaging.ServiceBus.ServiceBusClient;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingServiceBusReceiver
{
    // Pins ServiceBusReceiver's teardown. The synchronous Dispose() closes the inner receiver WITHOUT
    // awaiting it — a synchronous method cannot await — but the close attempt must still be OBSERVED: an
    // abandoned close task that faults is an unobserved failure nobody ever hears about, on the one path
    // where there is no rebuilt receiver left to make up for it.
    //
    // Every fact here is driven through the internal receiver-factory seam, so no broker is contacted: a
    // plain unit-constructed receiver has a null inner receiver, and resolving the internal InnerReceiver
    // accessor is what populates the field the teardown paths read.
    public class WhenDisposingServiceBusReceiver : Testing.Core.Context
    {
        private const string _connectionString =
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        private readonly RecordingLoggerCreator<ServiceBusReceiver> _logger;

        public WhenDisposingServiceBusReceiver() => _logger = New.Common().RecordingLogger<ServiceBusReceiver>();

        // The shared ServiceBusClient the receiver consumes from DI in production. A placeholder SAS
        // connection string opens no connection (the SDK connects lazily), so it is a valid stand-in here.
        private static ServiceBusClient CreateClient() => new ServiceBusClient(_connectionString);

        private static IBodyConverterFactory JsonFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        private static InboundBrokeredMessageFactory CreateInboundFactory()
            => new InboundBrokeredMessageFactory(JsonFactory(), Mock.Of<ILogger>());

        // A receiver initialized over the supplied inner port, with the inner port already RESOLVED: the
        // teardown paths read the inner receiver field, which stays null until something touches it. The
        // logger defaults to the recorder this fixture asserts on; a test supplies its own only to drive a
        // failing logging channel.
        private async Task<ServiceBusReceiver> InitializedSutOverAsync(IServiceBusMessageReceiver innerReceiver,
                                                                      ILogger<ServiceBusReceiver> logger = null)
        {
            var serviceBusOptions = new ServiceBusOptions { ConnectionString = _connectionString };
            var sut = new ServiceBusReceiver(CreateClient(),
                                             serviceBusOptions,
                                             new MessageBrokerOptions(),
                                             logger ?? _logger.Creation,
                                             CreateInboundFactory(),
                                             (_, __) => innerReceiver);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);
            _ = sut.InnerReceiver;
            return sut;
        }

        [Fact]
        public async Task MustCloseTheInnerReceiverOnTheSynchronousDisposePath()
        {
            var innerReceiver = new InMemoryServiceBusMessageReceiver();
            var sut = await InitializedSutOverAsync(innerReceiver);

            sut.Dispose();

            innerReceiver.CloseCount.Should().Be(1);
            sut.IsDisposed.Should().BeTrue();
        }

        [Fact]
        public async Task MustCloseTheInnerReceiverOnlyOnceAcrossRepeatedDisposes()
        {
            var innerReceiver = new InMemoryServiceBusMessageReceiver();
            var sut = await InitializedSutOverAsync(innerReceiver);

            sut.Dispose();
            sut.Dispose();

            innerReceiver.CloseCount.Should().Be(1);
        }

        // DisposeAsync awaits StopReceiver and then disposes with disposing: false, so the close it performs is
        // the AWAITED one and the synchronous close branch is never taken — the inner receiver is closed exactly
        // once either way.
        [Fact]
        public async Task MustCloseTheInnerReceiverOnlyOnceOnTheAsynchronousDisposePath()
        {
            var innerReceiver = new InMemoryServiceBusMessageReceiver();
            var sut = await InitializedSutOverAsync(innerReceiver);

            await sut.DisposeAsync();

            innerReceiver.CloseCount.Should().Be(1);
            sut.IsDisposed.Should().BeTrue();
        }

        // The fact this file exists for: the unawaited close is observed. The close task is already faulted when
        // the receiver hands it to its continuation, so the warning is recorded before Dispose returns and no
        // assertion depends on timing.
        [Fact]
        public async Task MustLogAFaultingUnawaitedCloseAsAWarning()
        {
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver());

            sut.Dispose();

            _logger.CountOf(LogLevel.Warning).Should().Be(1);
        }

        [Fact]
        public async Task MustNotThrowWhenTheUnawaitedCloseFaults()
        {
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver());

            Action disposing = () => sut.Dispose();

            disposing.Should().NotThrow();
        }

        // The OTHER fault shape, and the one the continuation structurally cannot see: a close that throws
        // SYNCHRONOUSLY, before any task exists to attach a continuation to. Every production
        // IServiceBusMessageReceiver implements CloseAsync as an `async` method, which captures its failure into
        // the returned task and so can never take this path — but the observed-close contract is stated on the
        // helper, not on its callers, so it must hold for any implementation of the port rather than for the
        // three that exist today.
        [Fact]
        public async Task MustLogASynchronouslyThrowingCloseAsAWarning()
        {
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver(CloseEnding.SynchronousThrow));

            sut.Dispose();

            _logger.CountOf(LogLevel.Warning).Should().Be(1);
        }

        // A close failure must not strand the disposal itself. The transition that nulls the inner receiver and
        // latches the disposed flag runs AFTER the close is started, so a synchronous throw escaping the close
        // would leave the receiver reporting undisposed and re-closable — the second dispose would then close a
        // second time.
        [Fact]
        public async Task MustCompleteTheDisposedTransitionWhenTheCloseThrowsSynchronously()
        {
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver(CloseEnding.SynchronousThrow));

            Action disposing = () => sut.Dispose();

            disposing.Should().NotThrow();
            sut.IsDisposed.Should().BeTrue();
        }

        // A CANCELLED close is not a clean teardown: the close was abandoned partway, so whatever the receiver
        // held is in the same orphaned state a faulted close leaves it in. Reporting only the faulted ending
        // reads an aborted close as a success.
        [Fact]
        public async Task MustLogACancelledUnawaitedCloseAsAWarning()
        {
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver(CloseEnding.CancelledTask));

            sut.Dispose();

            _logger.CountOf(LogLevel.Warning).Should().Be(1);
            sut.IsDisposed.Should().BeTrue();
        }

        // A close that returns NO task at all is a close whose outcome can never be observed — there is nothing
        // to observe it through. That is a reportable teardown failure, not a reason to fail the disposal.
        [Fact]
        public async Task MustLogACloseThatReturnsNoTaskAsAWarning()
        {
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver(CloseEnding.NoTask));

            Action disposing = () => sut.Dispose();

            disposing.Should().NotThrow();
            _logger.CountOf(LogLevel.Warning).Should().Be(1);
            sut.IsDisposed.Should().BeTrue();
        }

        // The reporting channel itself failing must not become the caller's problem: the logger is how a close
        // failure is reported, so a logger that throws has nowhere left to report to, and the disposal still has
        // to land.
        [Fact]
        public async Task MustCompleteTheDisposedTransitionWhenTheLoggingChannelThrows()
        {
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver(CloseEnding.SynchronousThrow),
                                                    new ThrowingLogger<ServiceBusReceiver>());

            Action disposing = () => sut.Dispose();

            disposing.Should().NotThrow();
            sut.IsDisposed.Should().BeTrue();
        }

        // The other half of the partition: reporting by default must not make a SUCCESSFUL close look like a
        // failure. Only the success ending is exempt, and it must stay exempt.
        [Fact]
        public async Task MustNotLogAWarningWhenTheUnawaitedCloseSucceeds()
        {
            var sut = await InitializedSutOverAsync(new InMemoryServiceBusMessageReceiver());

            sut.Dispose();

            _logger.CountOf(LogLevel.Warning).Should().Be(0);
        }

        // Every way the port's Task-returning CloseAsync can end WITHOUT succeeding. FaultedTask is how every
        // production implementation fails — each is an `async` method, which captures its failure into the
        // returned task — and the other three are endings the signature permits that no production
        // implementation produces today.
        private enum CloseEnding
        {
            FaultedTask,
            SynchronousThrow,
            CancelledTask,
            NoTask,
        }

        // An inner port whose close ends in a chosen non-success shape. Moq cannot stand in here — the
        // production assembly's DynamicProxyGenAssembly2 grant does not extend to this internal port over an
        // internal type.
        private sealed class FaultingCloseMessageReceiver : IServiceBusMessageReceiver
        {
            private readonly CloseEnding _closeEnding;

            public FaultingCloseMessageReceiver(CloseEnding closeEnding = CloseEnding.FaultedTask)
                => _closeEnding = closeEnding;

            public bool IsClosedOrClosing => false;

            public Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
                => Task.FromResult<ServiceBusReceivedMessage>(null);

            public Task<ServiceBusSettlementOutcome> CompleteAsync(ServiceBusReceivedMessage message)
                => Task.FromResult(ServiceBusSettlementOutcome.Settled);

            public Task<ServiceBusSettlementOutcome> AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
                => Task.FromResult(ServiceBusSettlementOutcome.Settled);

            public Task<ServiceBusSettlementOutcome> DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
                => Task.FromResult(ServiceBusSettlementOutcome.Settled);

            public void DeliveryReleased(ServiceBusReceivedMessage message) { }

            public Task CloseAsync()
            {
                switch (_closeEnding)
                {
                    case CloseEnding.SynchronousThrow:
                        throw CreateCloseFailure();
                    case CloseEnding.CancelledTask:
                        return Task.FromCanceled(new CancellationToken(canceled: true));
                    case CloseEnding.NoTask:
                        return null;
                    default:
                        return Task.FromException(CreateCloseFailure());
                }
            }

            private static ServiceBusException CreateCloseFailure()
                => new ServiceBusException("close failed", ServiceBusFailureReason.ServiceCommunicationProblem);
        }

        // A logger whose every write fails. The reporter IS the observation channel, so a failure of the
        // channel has nowhere to be reported; what must survive it is the caller's teardown.
        private sealed class ThrowingLogger<T> : ILogger<T>
        {
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
                => throw new InvalidOperationException("the logging channel failed");
        }
    }
}
