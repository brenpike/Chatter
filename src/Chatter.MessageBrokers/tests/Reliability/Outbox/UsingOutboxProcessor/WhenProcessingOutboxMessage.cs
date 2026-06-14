using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingOutboxProcessor
{
    public class WhenProcessingOutboxMessage : Testing.Core.Context
    {
        private const string Infra = "test-infrastructure";
        private const string ContentType = "application/json";

        private readonly Mock<IMessagingInfrastructureProvider> _infrastructureProvider = new Mock<IMessagingInfrastructureProvider>();
        private readonly Mock<IMessagingInfrastructureDispatcher> _dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
        private readonly Mock<ILogger<OutboxProcessor>> _logger = new Mock<ILogger<OutboxProcessor>>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IPollableOutboxStore> _outbox = new Mock<IPollableOutboxStore>();
        private readonly OutboxProcessor _sut;

        public WhenProcessingOutboxMessage()
        {
            _infrastructureProvider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(_dispatcher.Object);
            _bodyConverter.SetupGet(c => c.ContentType).Returns(ContentType);
            _bodyConverter.Setup(c => c.GetBytes(It.IsAny<string>())).Returns(new byte[] { 1, 2, 3 });
            _bodyConverterFactory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(_bodyConverter.Object);

            // INVARIANT: OutboxProcessor.Process casts the outbox to IUnitOfWork and dispatches
            // inside ExecuteAsync's callback; the mock MUST invoke that callback or dispatch never fires.
            _outbox.As<IUnitOfWork>()
                   .Setup(u => u.ExecuteAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()))
                   .Returns<Func<CancellationToken, Task>, TransactionContext, CancellationToken>((operation, _, ct) => operation(ct));

            _sut = new OutboxProcessor(_infrastructureProvider.Object, _logger.Object, _bodyConverterFactory.Object, _outbox.Object);
        }

        // The MessageContext column persists a Newtonsoft-serialized IDictionary<string, object> as a
        // JSON object string. Process deserializes it via JsonConvert.DeserializeObject and then performs
        // hard (string) casts on the ContentType (:43) and InfrastructureType (:48) values. Under Newtonsoft
        // those values deserialize to System.String, so the casts succeed.
        //
        // ORACLE: under System.Text.Json, DeserializeObject<IDictionary<string, object>> yields JsonElement
        // values, and the (string) casts at :43 and :48 throw InvalidCastException. Process swallows that
        // into _logger.LogError, so dispatch would silently never fire. These positive-dispatch assertions
        // are what make that future break visible — a "does not throw" assertion alone would still pass.
        private static string NewtonsoftSerializedContext()
            => $"{{\"{MessageContext.ContentType}\":\"{ContentType}\",\"{MessageContext.InfrastructureType}\":\"{Infra}\"}}";

        private static OutboxMessage CreateOutboxMessage()
            => new OutboxMessage
            {
                Id = 1,
                MessageId = "message-id",
                Destination = "destination",
                // Left empty so Process falls through to the (string) cast on messageContext[ContentType] at :43.
                MessageContentType = null,
                MessageContext = NewtonsoftSerializedContext(),
                MessageBody = "message-body",
            };

        [Fact]
        public async Task MustResolveDispatcherUsingInfrastructureTypeFromDeserializedContext()
        {
            await _sut.Process(CreateOutboxMessage());

            _infrastructureProvider.Verify(p => p.GetDispatcher(Infra), Times.Once);
        }

        [Fact]
        public async Task MustResolveBodyConverterUsingContentTypeFromDeserializedContext()
        {
            await _sut.Process(CreateOutboxMessage());

            _bodyConverterFactory.Verify(f => f.CreateBodyConverter(ContentType), Times.Once);
        }

        [Fact]
        public async Task MustDispatchOutboundMessageToInfrastructure()
        {
            await _sut.Process(CreateOutboxMessage());

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
        }

        [Fact]
        public async Task MustMarkOutboxMessageProcessed()
        {
            var message = CreateOutboxMessage();

            await _sut.Process(message);

            _outbox.Verify(o => o.UpdateProcessedDate(message, It.IsAny<CancellationToken>()), Times.Once);
        }

        // REGRESSION ORACLE: production writers (InMemory/EF SendToOutbox) serialize the entire
        // IDictionary<string, object> MessageContext, which legitimately holds non-string values —
        // an integer ReceiveAttempts (SSB receive/deadletter), a TimeSpan TimeToLive, and a DateTime
        // ScheduledEnqueueTimeUtc (Azure). Deserializing that row back to Dictionary<string, string>
        // threw JsonException on the numeric value; Process swallows it into LogError, silently
        // stranding a valid row (never dispatched, never marked processed). This context is built the
        // same way the writers build it — JsonSerializer.Serialize over an IDictionary<string, object>
        // with ChatterJson.Options — so it pins the real wire format, and the positive-dispatch +
        // processed assertions make any future re-break of the materialization visible.
        private static OutboxMessage CreateOutboxMessageWithNonStringContextValues()
        {
            var context = new System.Collections.Generic.Dictionary<string, object>
            {
                [MessageContext.ContentType] = ContentType,
                [MessageContext.InfrastructureType] = Infra,
                [MessageContext.ReceiveAttempts] = 3,
                [MessageContext.TimeToLive] = TimeSpan.FromMinutes(5),
                ["ScheduledEnqueueTimeUtc"] = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc),
            };

            return new OutboxMessage
            {
                Id = 2,
                MessageId = "message-id",
                Destination = "destination",
                MessageContentType = null,
                MessageContext = System.Text.Json.JsonSerializer.Serialize(context, ChatterJson.Options),
                MessageBody = "message-body",
            };
        }

        [Fact]
        public async Task MustDispatchOutboxMessageWhenContextContainsNonStringValues()
        {
            await _sut.Process(CreateOutboxMessageWithNonStringContextValues());

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
        }

        [Fact]
        public async Task MustMarkProcessedWhenContextContainsNonStringValues()
        {
            var message = CreateOutboxMessageWithNonStringContextValues();

            await _sut.Process(message);

            _outbox.Verify(o => o.UpdateProcessedDate(message, It.IsAny<CancellationToken>()), Times.Once);
        }

        // STRUCTURED-VALUE REPLAY FIDELITY (the "all areas" mandate for the outbox seam): a MessageContext
        // value that is itself a STRUCTURED object survives the persist -> replay round-trip materialized
        // to a navigable Dictionary<string, object> with CLR-typed leaves (NOT a raw JsonElement). The
        // OutboxProcessor builds the replayed OutboundBrokeredMessage from
        // MessageContext.MaterializePersistedContext(message.MessageContext), whose values are driven by
        // the global MaterializingObjectConverter on ChatterJson.Options. Capturing the dispatched message
        // exposes the materialized context so a regression that re-surfaced a JsonElement here fails.
        [Fact]
        public async Task MustReplayStructuredContextValueMaterializedOnDispatch()
        {
            var context = new System.Collections.Generic.Dictionary<string, object>
            {
                [MessageContext.ContentType] = ContentType,
                [MessageContext.InfrastructureType] = Infra,
                ["structured"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["id"] = 1,
                    ["name"] = "abc",
                },
            };

            var message = new OutboxMessage
            {
                Id = 3,
                MessageId = "message-id",
                Destination = "destination",
                MessageContentType = null,
                MessageContext = System.Text.Json.JsonSerializer.Serialize(context, ChatterJson.Options),
                MessageBody = "message-body",
            };

            OutboundBrokeredMessage dispatched = null;
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                       .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => dispatched = m)
                       .Returns(Task.CompletedTask);

            await _sut.Process(message);

            dispatched.Should().NotBeNull();
            dispatched.MessageContext.Should().ContainKey("structured");

            var structured = dispatched.MessageContext["structured"]
                .Should().BeAssignableTo<System.Collections.Generic.IDictionary<string, object>>().Subject;
            structured["id"].Should().BeOfType<long>().And.Be(1L);
            structured["name"].Should().BeOfType<string>().And.Be("abc");
        }
    }
}
