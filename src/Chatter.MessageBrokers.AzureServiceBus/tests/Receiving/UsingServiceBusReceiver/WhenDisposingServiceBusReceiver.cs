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
        // teardown paths read the inner receiver field, which stays null until something touches it.
        private async Task<ServiceBusReceiver> InitializedSutOverAsync(IServiceBusMessageReceiver innerReceiver)
        {
            var serviceBusOptions = new ServiceBusOptions { ConnectionString = _connectionString };
            var sut = new ServiceBusReceiver(CreateClient(),
                                             serviceBusOptions,
                                             new MessageBrokerOptions(),
                                             _logger.Creation,
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
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver(throwSynchronously: true));

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
            var sut = await InitializedSutOverAsync(new FaultingCloseMessageReceiver(throwSynchronously: true));

            Action disposing = () => sut.Dispose();

            disposing.Should().NotThrow();
            sut.IsDisposed.Should().BeTrue();
        }

        // An inner port whose close fails. By default it fails the way every production one does: CloseAsync is an
        // async method, so its failure arrives as a FAULTED TASK. Constructed with throwSynchronously it fails the
        // other legal way for the port's Task-returning signature — throwing before a task is ever returned. Moq
        // cannot stand in here — the production assembly's DynamicProxyGenAssembly2 grant does not extend to this
        // internal port over an internal type.
        private sealed class FaultingCloseMessageReceiver : IServiceBusMessageReceiver
        {
            private readonly bool _throwSynchronously;

            public FaultingCloseMessageReceiver(bool throwSynchronously = false)
                => _throwSynchronously = throwSynchronously;

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
                var closeFailure = new ServiceBusException("close failed", ServiceBusFailureReason.ServiceCommunicationProblem);
                return _throwSynchronously ? throw closeFailure : Task.FromException(closeFailure);
            }
        }
    }
}
