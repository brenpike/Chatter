using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelay
{
    /// <summary>
    /// The #361 give-up: an admitted Outbox Document whose OWN persisted bytes prove it can never be reconstructed
    /// into a brokered message is stamped an Undeliverable Outbox Document instead of being published, so the lease
    /// advances past it rather than wedging on it forever.
    /// </summary>
    /// <remarks>
    /// The class runs in the diagnostics collection because the give-up's undeliverable count is asserted through a
    /// process-global <c>MeterListener</c>, which an absence test running concurrently would otherwise observe.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenADocumentCannotBePublished : Testing.Core.Context
    {
        private const string MessageId = "msg-1";
        private const string TenantId = "tenant-1";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        // A wire-faithful ADMITTED outbox document (outbox discriminator, pending status, the deterministic
        // ForOutbox(MessageId) id) carrying exactly the fields the reconstruction reads. Each parameter lets ONE field
        // diverge so a test isolates a single contract violation; a null parameter OMITS the property entirely.
        private static JsonElement OutboxDocument(
            string destination = "orders",
            string messageBody = "{}",
            string messageContentType = "application/json",
            string messageContext = "{}")
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = CosmosItemId.ForOutbox(MessageId),
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = MessageId,
                ["tenantId"] = TenantId,
            };
            AddWhenPresent(node, CosmosOutboxDocument.DestinationField, destination);
            AddWhenPresent(node, CosmosOutboxDocument.MessageBodyField, messageBody);
            AddWhenPresent(node, CosmosOutboxDocument.MessageContentTypeField, messageContentType);
            AddWhenPresent(node, CosmosOutboxDocument.MessageContextField, messageContext);
            return Parse(node.ToJsonString());
        }

        private static void AddWhenPresent(JsonObject node, string propertyName, string value)
        {
            if (value is not null)
            {
                node[propertyName] = value;
            }
        }

        // A persisted MessageContext carrying one entry, serialized the way the outbox persists it: a JSON string field
        // whose content is the serialized context object.
        private static string SerializedContext(string key, JsonNode value)
            => new JsonObject { [key] = value }.ToJsonString();

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static IBodyConverterFactory BodyConverterFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        // A provider whose dispatcher records every dispatched message and which records every messaging system it was
        // asked to resolve a dispatcher for; the recorded lists are the publish ledger and the dispatch-resolution ledger.
        private static (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published, List<string> requestedMessagingSystems) RecordingProvider()
        {
            var published = new List<OutboundBrokeredMessage>();
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => published.Add(m))
                      .Returns(Task.CompletedTask);

            var requestedMessagingSystems = new List<string>();
            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>()))
                    .Callback<string>(requestedMessagingSystems.Add)
                    .Returns(dispatcher.Object);
            return (provider.Object, published, requestedMessagingSystems);
        }

        // A container that records each PatchItemAsync call (id, partition key, ops) and returns a benign response.
        private static (Mock<Container> container, List<(string id, PartitionKey pk, IReadOnlyList<PatchOperation> ops)> patches) RecordingContainer()
        {
            var patches = new List<(string, PartitionKey, IReadOnlyList<PatchOperation>)>();
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Callback<string, PartitionKey, IReadOnlyList<PatchOperation>, PatchItemRequestOptions, CancellationToken>(
                        (id, pk, ops, _, __) => patches.Add((id, pk, ops)))
                     .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            return (container, patches);
        }

        [Fact]
        public async Task MustNotPublishADocumentThatViolatesTheOutboxDocumentContract()
        {
            var (provider, published, _) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            // No destination: OutboundBrokeredMessage's constructor rejects one, so this document can never be
            // reconstructed no matter how often it is redrained.
            await relay.ProcessChangeAsync(OutboxDocument(destination: null), container.Object, PartitionKeyPath);

            published.Should().BeEmpty("an undeliverable stamp happens INSTEAD of a publish, never after one");
        }

        [Fact]
        public async Task MustStampUndeliverableInASingleOpPatchThatCarriesNoTtl()
        {
            var (provider, _, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            await relay.ProcessChangeAsync(OutboxDocument(destination: null), container.Object, PartitionKeyPath);

            (string id, PartitionKey pk, IReadOnlyList<PatchOperation> ops) = patches.Should().ContainSingle().Subject;
            id.Should().Be(CosmosItemId.ForOutbox(MessageId), "the stamp is keyed on the document id read off the change-feed item");
            pk.Should().Be(new PartitionKey(TenantId), "the stamp targets the document's own logical partition");
            ops.Should().ContainSingle("an Undeliverable Outbox Document is the evidence of the defect: it carries no ttl and is never deleted");
            ops[0].Path.Should().Be("/" + CosmosOutboxDocument.StatusField);
            ops[0].As<PatchOperation<string>>().Value.Should().Be(CosmosOutboxDocument.StatusUndeliverable);
        }

        [Fact]
        public async Task MustCountTheUndeliverableDocumentAndRecordNoDrainOutcome()
        {
            var (provider, _, _) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                await relay.ProcessChangeAsync(OutboxDocument(destination: null), container.Object, PartitionKeyPath);

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainUndeliverableInstrumentName)
                          .Should().ContainSingle("the give-up is reported once for the document it was taken on")
                          .Which.Value.Should().Be(1);
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName)
                          .Should().BeEmpty("the document never resolved to a publish decision, so it has no drain outcome — that vocabulary is closed");
            }
        }

        [Fact]
        public async Task MustReportTheFullViolationTextAtError()
        {
            var (provider, _, _) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var logger = new RecordingLogger();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), new GuardedRelayLog(logger));

            await relay.ProcessChangeAsync(OutboxDocument(destination: null), container.Object, PartitionKeyPath);

            (LogLevel Level, string Message) entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Error);
            entry.Message.Should().Contain(CosmosOutboxDocument.DestinationField,
                "the full violation text is the only channel a meter-less application has, and the count carries no violation attribute");
            entry.Message.Should().Contain(MessageId, "an operator has to be able to find the document the relay gave up on");
        }

        [Fact]
        public async Task MustPropagateAStampFailureRatherThanReportAGiveUpThatDidNotHappen()
        {
            // A give-up cannot be recorded in a container that is not accepting writes. The stamp is issued FIRST so a
            // failing one propagates BEFORE anything reports it: the batch is not checkpointed, the identical immutable
            // document re-surfaces, and the next pass re-evaluates it to the identical verdict — the give-up self-heals
            // rather than being lost.
            var (provider, _, _) = RecordingProvider();
            var stampFailure = new InvalidOperationException("the container is unavailable");
            Mock<Container> container = ThrowingContainer(stampFailure);
            var logger = new RecordingLogger();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), new GuardedRelayLog(logger));

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                Func<Task> act = () => relay.ProcessChangeAsync(OutboxDocument(destination: null), container.Object, PartitionKeyPath);

                (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(stampFailure);
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainUndeliverableInstrumentName)
                          .Should().BeEmpty("nothing may report a give-up the container never accepted");
                logger.Entries.Should().BeEmpty("nothing may report a give-up the container never accepted");
            }
        }

        [Fact]
        public async Task MustGiveUpRatherThanFaultOnANonStringPersistedContentType()
        {
            // The document carries no content type of its own and its persisted context carries a NON-STRING one. The
            // contract reads that value AS a string rather than casting to one, so this resolves no content type and is
            // a named violation instead of the InvalidCastException the drain used to fault the whole batch with.
            var (provider, published, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement document = OutboxDocument(
                messageContentType: null,
                messageContext: SerializedContext(MessageContext.ContentType, 7));
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            published.Should().BeEmpty();
            patches.Should().ContainSingle("a document whose content type resolves to nothing is undeliverable, not a batch fault")
                   .Which.ops.Should().ContainSingle()
                   .Which.As<PatchOperation<string>>().Value.Should().Be(CosmosOutboxDocument.StatusUndeliverable);
        }

        [Fact]
        public async Task MustGiveUpRatherThanFaultOnANonStringPersistedMessagingSystem()
        {
            // The persisted context names a NON-STRING messaging system. The dispatch reads that value AS a string
            // rather than casting to one, and the contract classifies the non-string kind, so this is a named
            // violation instead of the InvalidCastException that used to wedge the lease on this document forever.
            var (provider, published, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var logger = new RecordingLogger();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), new GuardedRelayLog(logger));

            JsonElement document = OutboxDocument(messageContext: SerializedContext(MessageContext.InfrastructureType, 7));

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainUndeliverableInstrumentName)
                          .Should().ContainSingle("the give-up is reported once for the document it was taken on")
                          .Which.Value.Should().Be(1);
            }

            published.Should().BeEmpty("an undeliverable document is never published");
            patches.Should().ContainSingle().Which.ops.Should().ContainSingle()
                   .Which.As<PatchOperation<string>>().Value.Should().Be(CosmosOutboxDocument.StatusUndeliverable);
            (LogLevel Level, string Message) entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Error);
            entry.Message.Should().Contain(MessageContext.InfrastructureType);
        }

        [Fact]
        public async Task MustPublishADocumentWhoseContextNamesNoMessagingSystem()
        {
            // An ABSENT messaging system is a HOST concern, never a document defect: the provider answers a null with
            // its default infrastructure or a KeyNotFoundException a redeploy fixes. The document must reach the
            // dispatcher exactly as it always has.
            var (provider, published, requestedMessagingSystems) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            await relay.ProcessChangeAsync(OutboxDocument(), container.Object, PartitionKeyPath);

            published.Should().ContainSingle("naming no messaging system is not a violation the document's own bytes prove");
            requestedMessagingSystems.Should().ContainSingle()
                                     .Which.Should().BeNull("the dispatcher is resolved with exactly what the context named — nothing");
            patches.Should().ContainSingle().Which.ops.Should().HaveCount(2, "a published document is stamped delivered, never undeliverable");
        }

        // A container whose every patch fails, standing in for one that is not accepting writes.
        private static Mock<Container> ThrowingContainer(Exception toThrow)
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(toThrow);
            return container;
        }

        /// <summary>An <see cref="ILogger"/> recorder that captures the rendered message of each log call.</summary>
        private sealed class RecordingLogger : ILogger
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel, string)>();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();

                public void Dispose()
                {
                }
            }
        }
    }
}
