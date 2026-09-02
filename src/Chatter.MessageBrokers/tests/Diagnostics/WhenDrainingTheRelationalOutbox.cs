using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The RELATIONAL outbox drain observed as its own send hop: the row is published to broker infrastructure
    /// minutes after the transaction that wrote it, in another process, where it can fail entirely on its own.
    /// </summary>
    /// <remarks>
    /// THE LOAD-BEARING ASSERTION is the parenting one. The trace is NOT severed at this hop today — the
    /// <c>traceparent</c> is injected at message construction, persisted with the message context and materialized on
    /// drain — so the only thing missing was observation. Severing is the hazard that ADDING observation could
    /// introduce: a drain span parented off <see cref="Activity.Current"/> would adopt the DRAIN LOOP's ambient
    /// activity and then overwrite the persisted write-time record with it, reporting that the poll caused the message
    /// when the write did (ADR-0010 D6). Every parenting test below therefore runs with a deliberately-different
    /// ambient activity attached, so a regression to ambient parenting fails rather than passes quietly.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenDrainingTheRelationalOutbox : Testing.Core.Context
    {
        // The W3C Trace Context specification's own example traceparent, standing in for the value the outbox row
        // persisted at write time.
        private const string PersistedTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        private const string PersistedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        private const string PersistedSpanId = "00f067aa0ba902b7";
        private const string Infra = "outbox-drain-infrastructure";
        private const string Destination = "outbox-drain-destination";
        private const string ContentType = "application/json";

        /// <summary>A drain publishes exactly one row, so the batch count on its send span is always one.</summary>
        private const int DrainedMessageCount = 1;

        private readonly Mock<IMessagingInfrastructureProvider> _infrastructureProvider = new Mock<IMessagingInfrastructureProvider>();
        private readonly Mock<IMessagingInfrastructureDispatcher> _dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
        private readonly Mock<ILogger<OutboxProcessor>> _logger = new Mock<ILogger<OutboxProcessor>>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IBrokeredMessageOutbox> _outbox = new Mock<IBrokeredMessageOutbox>();
        private readonly OutboxProcessor _sut;

        private OutboundBrokeredMessage _dispatched;

        public WhenDrainingTheRelationalOutbox()
        {
            _infrastructureProvider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(_dispatcher.Object);
            _bodyConverter.SetupGet(c => c.ContentType).Returns(ContentType);
            _bodyConverter.Setup(c => c.GetBytes(It.IsAny<string>())).Returns(new byte[] { 1, 2, 3 });
            _bodyConverterFactory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(_bodyConverter.Object);

            // Capturing the dispatched message is what exposes the context that actually reached broker
            // infrastructure, which is the only place the wire write is observable. Returning a completed task
            // matters: an unconfigured Moq method returns a null Task, and awaiting it would fail the dispatch and
            // make every success assertion below read a FAILED drain.
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                       .Callback<OutboundBrokeredMessage, TransactionContext>((outbound, _) => _dispatched = outbound)
                       .Returns(Task.CompletedTask);

            // OutboxProcessor.Process casts the outbox to IUnitOfWork and IPollableOutboxStore at the consumption
            // site, so the mock must implement both for the dispatch to be reached at all.
            _outbox.As<IPollableOutboxStore>();
            _outbox.As<IUnitOfWork>()
                   .Setup(u => u.ExecuteAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()))
                   .Returns<Func<CancellationToken, Task>, TransactionContext, CancellationToken>((operation, _, token) => operation(token));

            _sut = new OutboxProcessor(_infrastructureProvider.Object, _logger.Object, _bodyConverterFactory.Object, _outbox.Object);
        }

        [Fact]
        public async Task MustParentTheDrainSpanToThePersistedWriteTimeTraceContext()
        {
            // THE FALSE-CAUSALITY REGRESSION GUARD. The ambient activity here is the drain loop's; the drain span
            // must be a child of the context the WRITER persisted and never of the activity the poll happens to be
            // running under.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await _sut.Process(CreateDrainedMessage(PersistedTraceParent));

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                drainSpan.TraceId.ToHexString().Should().Be(PersistedTraceId);
                drainSpan.ParentSpanId.ToHexString().Should().Be(PersistedSpanId);
                drainSpan.Parent.Should().BeNull("the causal parent is a persisted trace context, not a running Activity");
                drainSpan.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId,
                    "adopting the drain loop's ambient trace would report that the poll caused the message when the write did");
                drainSpan.Links.Should().ContainSingle().Which.Context.Should().Be(foreignInstrumentation.ForeignActivity.Context,
                    "the ambient rides along as a LINK, never promoted to parent (ADR-0010 D6)");
            }
        }

        [Fact]
        public async Task MustStartAFreshRootWhenTheDrainedRowCarriesNoPersistedTraceContext()
        {
            // A row written while diagnostics were off carries no traceparent. Absence must stay absence: falling
            // back to the ambient here is the same false causality the test above rejects, and it is worse, because
            // a fresh root is at least honest about having no known parent.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await _sut.Process(CreateDrainedMessage(persistedTraceParent: null));

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                drainSpan.Parent.Should().BeNull();
                drainSpan.ParentSpanId.Should().Be(default(ActivitySpanId));
                drainSpan.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId);
                drainSpan.Links.Should().ContainSingle().Which.Context.Should().Be(foreignInstrumentation.ForeignActivity.Context);
            }
        }

        [Fact]
        public async Task MustEmitASendSpanAndASentMessagesMeasurementForADrain()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                await _sut.Process(CreateDrainedMessage(PersistedTraceParent));

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                drainSpan.Kind.Should().Be(ActivityKind.Producer);
                drainSpan.DisplayName.Should().Be(BrokerDiagnostics.OperationTypes.Send + " " + Destination);
                drainSpan.GetTagItem(BrokerDiagnostics.MessagingSystem).Should().Be(Infra);
                drainSpan.GetTagItem(BrokerDiagnostics.DestinationName).Should().Be(Destination);
                drainSpan.GetTagItem(BrokerDiagnostics.OperationType).Should().Be(BrokerDiagnostics.OperationTypes.Send);
                drainSpan.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(DrainedMessageCount);

                var sent = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
                sent.Value.Should().Be(DrainedMessageCount);
                sent.TryGetTag(BrokerDiagnostics.MessagingSystem, out var messagingSystem).Should().BeTrue();
                messagingSystem.Should().Be(Infra);
                sent.TryGetTag(BrokerDiagnostics.DestinationName, out var destination).Should().BeTrue();
                destination.Should().Be(Destination);
            }
        }

        [Fact]
        public async Task MustRecordTheDurationOfTheDrainHop()
        {
            // The whole point of observing this hop: how long the publish took, minutes after the write, is not
            // derivable from anything the write-time span recorded.
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                await _sut.Process(CreateDrainedMessage(PersistedTraceParent));

                var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;
                duration.Value.Should().BeGreaterThanOrEqualTo(0);
                duration.TryGetTag(BrokerDiagnostics.OperationType, out var operationType).Should().BeTrue();
                operationType.Should().Be(BrokerDiagnostics.OperationTypes.Send);
                duration.TryGetTag(BrokerDiagnostics.DestinationName, out var destination).Should().BeTrue();
                destination.Should().Be(Destination);
            }
        }

        [Fact]
        public async Task MustRecordAPublishFailureOnTheDrainSpanAndTheDrainMetric()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var failure = new DiagnosticsProbeException("the broker publish failed deliberately");
                _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null)).ThrowsAsync(failure);

                await _sut.Awaiting(processor => processor.Process(CreateDrainedMessage(PersistedTraceParent)))
                          .Should().NotThrowAsync("observing the drain does not change how it handles a failed publish");

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                drainSpan.Status.Should().Be(ActivityStatusCode.Error);
                drainSpan.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(DiagnosticsProbeException).FullName);

                var duration = meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle().Subject;
                duration.TryGetTag(ChatterTelemetryTags.ErrorType, out var errorType).Should().BeTrue();
                errorType.Should().Be(typeof(DiagnosticsProbeException).FullName);
            }
        }

        [Fact]
        public async Task MustWriteTheDrainSpanTraceContextOntoTheDispatchedMessage()
        {
            // The drain span is the hop that actually put the message on the broker, so it is what a downstream
            // receive must parent to. It carries the write-time trace id, so the chain stays contiguous.
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await _sut.Process(CreateDrainedMessage(PersistedTraceParent));

                var drainSpan = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                _dispatched.Should().NotBeNull();
                _dispatched.MessageContext[TraceContextHeaders.TraceParent].Should().Be(drainSpan.Id);
                drainSpan.TraceId.ToHexString().Should().Be(PersistedTraceId);
            }
        }

        [Fact]
        public async Task MustHandTheAmbientActivityBackAfterTheDrain()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                await _sut.Process(CreateDrainedMessage(PersistedTraceParent));

                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        [Fact]
        public async Task MustLeaveThePersistedTraceContextOnTheWireWhileDiagnosticsAreOff()
        {
            // THE OFF PATH, asserted here rather than in the shared absence suite so this step stays file-disjoint
            // from the one that owns that suite. An application that never opted in gets no wire write from the
            // drain and no ambient activity taken away from it (ADR-0010 R1, R2).
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                BrokerDiagnostics.IsEnabled.Should().BeFalse();

                await _sut.Process(CreateDrainedMessage(PersistedTraceParent));

                _dispatched.Should().NotBeNull("the drain still publishes when nothing is listening");
                _dispatched.MessageContext[TraceContextHeaders.TraceParent].Should().Be(PersistedTraceParent);
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        /// <summary>
        /// Builds the outbox row the drain replays, persisted exactly as the production writers persist it — the
        /// whole <c>IDictionary&lt;string, object&gt;</c> message context serialized with <c>ChatterJson.Options</c>.
        /// </summary>
        /// <param name="persistedTraceParent">The write-time <c>traceparent</c>, or <c>null</c> for a row written while diagnostics were off.</param>
        private static OutboxMessage CreateDrainedMessage(string persistedTraceParent)
        {
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.ContentType] = ContentType,
                [MessageContext.InfrastructureType] = Infra,
            };

            if (persistedTraceParent != null)
            {
                messageContext[TraceContextHeaders.TraceParent] = persistedTraceParent;
            }

            return new OutboxMessage
            {
                Id = 1,
                MessageId = "message-id",
                Destination = Destination,
                MessageContentType = null,
                MessageContext = JsonSerializer.Serialize(messageContext, ChatterJson.Options),
                MessageBody = "message-body",
            };
        }
    }
}
