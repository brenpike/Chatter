using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingOutboxProcessor
{
    /// <summary>
    /// Drains the REAL default in-memory Pollable Outbox Store through the REAL <see cref="OutboxProcessor"/>.
    /// </summary>
    /// <remarks>
    /// INVARIANT: no mock outbox. The sibling drain tests build their store as a mock and grant it the Unit of Work
    /// facet with <c>As&lt;IUnitOfWork&gt;()</c>, which manufactures the very facet whose absence broke the DI-default
    /// store: the cast in <see cref="OutboxProcessor.Process"/> threw into the swallowing catch, nothing was ever
    /// dispatched, and the suite stayed green over a dead default outbox. This class wires the concrete store DI
    /// registers so no facet can be faked, and the only test double is the messaging infrastructure the drain
    /// publishes to.
    /// ORACLE: every assertion reads the SINK - the messages the infrastructure dispatcher actually received, and
    /// the rows the store itself still returns from GetUnprocessedMessagesFromOutbox - never a copy of the drain's
    /// own condition and never the store's private state.
    /// </remarks>
    public class WhenDrainingTheDefaultInMemoryOutbox : Testing.Core.Context
    {
        private const string Infra = "test-infrastructure";
        private const string Destination = "destination";
        private const string MessageId = "message-id";
        private const string MessageBody = "payload";

        private readonly RecordingInfrastructureDispatcher _dispatcher = new RecordingInfrastructureDispatcher();
        private readonly InMemoryBrokeredMessageOutbox _outbox;
        private readonly IPollableOutboxStore _pollableStore;
        private readonly OutboxProcessor _sut;

        public WhenDrainingTheDefaultInMemoryOutbox()
        {
            _outbox = new InMemoryBrokeredMessageOutbox(New.Common().RecordingLogger<InMemoryBrokeredMessageOutbox>().Creation,
                                                        new ReliabilityOptions());
            _pollableStore = _outbox;

            var infrastructureProvider = new MessagingInfrastructureProvider(new IMessagingInfrastructure[] { new DispatchOnlyInfrastructure(Infra, _dispatcher) },
                                                                            New.Common().RecordingLogger<MessagingInfrastructureProvider>().Creation);
            var bodyConverterFactory = new BodyConverterFactory(new IBrokeredMessageBodyConverter[] { new JsonBodyConverter() });

            _sut = new OutboxProcessor(infrastructureProvider,
                                       New.Common().RecordingLogger<OutboxProcessor>().Creation,
                                       bodyConverterFactory,
                                       _outbox);
        }

        /// <summary>
        /// Writes one row the way a sender writes it - through the store's own SendToOutbox - and polls it back the
        /// way the drain polls it, so the row under test is one the production write path produced.
        /// </summary>
        private async Task<OutboxMessage> EnqueueAndPollOneRow()
        {
            var messageContext = new Dictionary<string, object> { [MessageContext.InfrastructureType] = Infra };
            var outbound = new OutboundBrokeredMessage(MessageId, MessageBody, messageContext, Destination, new JsonBodyConverter());

            await _outbox.SendToOutbox(outbound, null);

            return (await _pollableStore.GetUnprocessedMessagesFromOutbox()).Single();
        }

        private async Task<IEnumerable<OutboxMessage>> PollUnprocessedRows()
            => await _pollableStore.GetUnprocessedMessagesFromOutbox();

        // FACT 1 - HAPPY PATH (delivery). The sink is the infrastructure dispatcher: it holds the message ids it was
        // handed, so a drain that resolved no dispatcher, threw on a facet cast, or swallowed anything on the way
        // delivers nothing and this fails.
        [Fact]
        public async Task MustDispatchTheDrainedRowExactlyOnce()
        {
            var row = await EnqueueAndPollOneRow();

            await _sut.Process(row);

            _dispatcher.DeliveredMessageIds.Should().ContainSingle().Which.Should().Be(MessageId);
        }

        // FACT 1 - HAPPY PATH (the row leaves the poll). The sink is the store's own query: a dispatched row must no
        // longer be returned to a later poll, or the drain republishes it forever.
        [Fact]
        public async Task MustStopReturningTheDrainedRowAsUnprocessed()
        {
            var row = await EnqueueAndPollOneRow();

            await _sut.Process(row);

            (await PollUnprocessedRows()).Should().BeEmpty();
        }

        // FACT 2 - FAILURE IS RETRYABLE. A publish that never reached the broker must leave the row exactly as a
        // later poll needs to find it. The dispatch-attempt assertion keeps this non-vacuous: a drain that stopped
        // publishing at all would otherwise satisfy the unprocessed assertions.
        [Fact]
        public async Task MustLeaveTheRowUnprocessedWhenDispatchFails()
        {
            var row = await EnqueueAndPollOneRow();
            _dispatcher.FailureToThrow = new InvalidOperationException("the broker publish failed deliberately");

            await _sut.Process(row);

            _dispatcher.DispatchAttempts.Should().Be(1);
            _dispatcher.DeliveredMessageIds.Should().BeEmpty();
            (await PollUnprocessedRows()).Should().ContainSingle().Which.MessageId.Should().Be(MessageId);
            row.ProcessedFromOutboxAtUtc.Should().BeNull();
        }

        // FACT 2 - ERROR POSTURE. Process is driven by the outbox poll and by OutboxProcessingBehavior, so a broker
        // outage stays logged-and-swallowed rather than surfacing in the CQRS pipeline.
        [Fact]
        public async Task MustNotThrowWhenDispatchFails()
        {
            var row = await EnqueueAndPollOneRow();
            _dispatcher.FailureToThrow = new InvalidOperationException("the broker publish failed deliberately");

            Func<Task> process = () => _sut.Process(row);

            await process.Should().NotThrowAsync();
        }

        // FACT 3 - RECOVERY. The row a failed drain left behind is the row the next poll hands back, and a healthy
        // dispatcher then delivers THAT row and the store stops returning it. This is the whole point of leaving the
        // row unprocessed, so it is asserted end to end rather than inferred from fact 2.
        [Fact]
        public async Task MustDispatchTheSameRowOnTheNextDrainAfterAFailedDispatch()
        {
            var row = await EnqueueAndPollOneRow();
            _dispatcher.FailureToThrow = new InvalidOperationException("the broker publish failed deliberately");
            await _sut.Process(row);
            _dispatcher.FailureToThrow = null;

            var retriedRow = (await PollUnprocessedRows()).Single();
            await _sut.Process(retriedRow);

            retriedRow.MessageId.Should().Be(MessageId);
            _dispatcher.DispatchAttempts.Should().Be(2);
            _dispatcher.DeliveredMessageIds.Should().ContainSingle().Which.Should().Be(MessageId);
            (await PollUnprocessedRows()).Should().BeEmpty();
            retriedRow.ProcessedFromOutboxAtUtc.Should().NotBeNull();
        }

        /// <summary>
        /// The messaging infrastructure the drain publishes to, recording what it was handed. This is the delivery
        /// sink the facts read; <see cref="FailureToThrow"/> makes the publish fail the way a broker outage does.
        /// </summary>
        private sealed class RecordingInfrastructureDispatcher : IMessagingInfrastructureDispatcher
        {
            private readonly List<string> _deliveredMessageIds = new List<string>();

            public IReadOnlyList<string> DeliveredMessageIds => _deliveredMessageIds;
            public int DispatchAttempts { get; private set; }
            public Exception FailureToThrow { get; set; }

            public Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
                => throw new NotSupportedException("The outbox drain publishes one row per call and never uses the batch overload.");

            public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
            {
                DispatchAttempts++;

                if (FailureToThrow != null)
                {
                    return Task.FromException(FailureToThrow);
                }

                _deliveredMessageIds.Add(brokeredMessage.MessageId);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Registers <see cref="RecordingInfrastructureDispatcher"/> under the Messaging Infrastructure type the
        /// persisted row names, so the REAL <see cref="MessagingInfrastructureProvider"/> performs the lookup the
        /// drain performs. The receive and path-building members are unreachable from a drain and say so rather
        /// than answering null.
        /// </summary>
        private sealed class DispatchOnlyInfrastructure : IMessagingInfrastructure
        {
            public DispatchOnlyInfrastructure(string type, IMessagingInfrastructureDispatcher dispatchInfrastructure)
            {
                Type = type;
                DispatchInfrastructure = dispatchInfrastructure;
            }

            public string Type { get; }
            public IMessagingInfrastructureDispatcher DispatchInfrastructure { get; }
            public IMessagingInfrastructureReceiver ReceiveInfrastructure => throw new NotSupportedException("The outbox drain never receives.");
            public IBrokeredMessagePathBuilder PathBuilder => throw new NotSupportedException("The outbox drain publishes to the persisted destination and builds no path.");
        }
    }
}
