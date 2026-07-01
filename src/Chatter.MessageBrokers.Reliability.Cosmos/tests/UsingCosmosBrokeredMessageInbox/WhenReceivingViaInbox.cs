using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Inbox;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosBrokeredMessageInbox
{
    /// <summary>
    /// Characterizes the standalone, lease-less Cosmos inbox (#253, ADR-0009): a write-ahead CreateItemStream claim that
    /// runs the handler exactly once on a fresh claim (201), CONFIRMS a create-409 by point-reading the conflicting
    /// marker (skip only on a genuine same-id inbox marker; redeliver otherwise), fails loud on a missing id, and
    /// best-effort compensation-deletes the marker on a handler failure. Mocks <see cref="Container"/> directly (in-tree
    /// InternalsVisibleTo/DynamicProxy precedent) and drives the confirm read-back at zero wall-clock via the internal
    /// test-seam constructor.
    /// </summary>
    public class WhenReceivingViaInbox : Testing.Core.Context
    {
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/idempotencyKey" });

        private static IMessageBrokerContext Context(string messageId)
            => new MessageBrokerContext(messageId, Array.Empty<byte>(), null, "receiver", CancellationToken.None, new JsonBodyConverter());

        // Zero-wall-clock delay seam; records each requested backoff so the read-back budget can be asserted.
        private sealed class Clock
        {
            public List<TimeSpan> Delays { get; } = new List<TimeSpan>();
            public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
            {
                Delays.Add(delay);
                return Task.CompletedTask;
            }
        }

        private static CosmosBrokeredMessageInbox InboxOver(Container container, Clock clock, int? markerTimeToLive = null, int readBackMaxAttempts = 5)
            => new CosmosBrokeredMessageInbox(
                container,
                PartitionKeyPath,
                markerTimeToLive,
                readBackMaxAttempts,
                attempt => TimeSpan.FromMilliseconds(attempt),
                clock.Delay);

        private static ResponseMessage Response(HttpStatusCode statusCode, Stream content = null)
            => new ResponseMessage(statusCode) { Content = content };

        // A minimal confirm-read payload carrying only the two fields the confirmation inspects.
        private static Stream ConfirmPayload(string chatterType, string messageId)
            => new MemoryStream(Encoding.UTF8.GetBytes($"{{\"_chatterType\":\"{chatterType}\",\"MessageId\":\"{messageId}\"}}"), writable: false);

        private static Stream GenuineMarkerPayload(string messageId) => ConfirmPayload(CosmosItemId.InboxKind, messageId);

        private static void SetupCreate(Mock<Container> container, Func<ResponseMessage> onCreate)
            => container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(onCreate);

        private static void SetupRead(Mock<Container> container, Func<ResponseMessage> onRead)
            => container.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(onRead);

        [Fact]
        public async Task MustRunHandlerOnceOnFreshClaimWithNoReadBack()
        {
            var handlerRuns = 0;
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Created));

            await InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context("msg-1"), () =>
            {
                handlerRuns++;
                return Task.CompletedTask;
            });

            handlerRuns.Should().Be(1, "a fresh 201 claim runs the handler exactly once");
            container.Verify(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never, "a successful claim never runs the confirm read-back (cold-path-only)");
            container.Verify(c => c.DeleteItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never, "a successful handler never compensates");
        }

        [Fact]
        public async Task MustSkipHandlerOnRedeliveryWhenConflictConfirmsGenuineMarker()
        {
            var handlerRuns = 0;
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Conflict));
            SetupRead(container, () => Response(HttpStatusCode.OK, GenuineMarkerPayload("msg-1")));

            await InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context("msg-1"), () =>
            {
                handlerRuns++;
                return Task.CompletedTask;
            });

            handlerRuns.Should().Be(0, "a confirmed genuine-marker duplicate skips the handler");
        }

        [Fact]
        public async Task MustRedeliverWhenConflictingDocIsNotAnInboxMarker()
        {
            var handlerRuns = 0;
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Conflict));
            // An app-authored collision carrying a reserved-prefix id but the wrong discriminator is NOT a duplicate.
            SetupRead(container, () => Response(HttpStatusCode.OK, ConfirmPayload(CosmosItemId.OutboxKind, "msg-1")));

            Func<Task> act = () => InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context("msg-1"), () =>
            {
                handlerRuns++;
                return Task.CompletedTask;
            });

            await act.Should().ThrowAsync<InvalidOperationException>("a non-marker collision is not a confirmed duplicate — redeliver, never skip");
            handlerRuns.Should().Be(0);
        }

        [Fact]
        public async Task MustRedeliverWhenConflictingMarkerCarriesADifferentMessageId()
        {
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Conflict));
            // A genuine inbox marker but for a DIFFERENT message id is an id collision, not a duplicate of THIS message.
            SetupRead(container, () => Response(HttpStatusCode.OK, GenuineMarkerPayload("some-other-id")));

            Func<Task> act = () => InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context("msg-1"), () => Task.CompletedTask);

            await act.Should().ThrowAsync<InvalidOperationException>("a marker for a different message id cannot confirm THIS message as a duplicate");
        }

        [Fact]
        public async Task MustConvergeWithinBudgetWhenConflictingMarkerIsNotYetVisible()
        {
            var handlerRuns = 0;
            var clock = new Clock();
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Conflict));
            // The conflicting marker is not yet visible on the first read, then converges on the second.
            container.SetupSequence(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Response(HttpStatusCode.NotFound))
                     .ReturnsAsync(Response(HttpStatusCode.OK, GenuineMarkerPayload("msg-1")));

            await InboxOver(container.Object, clock, readBackMaxAttempts: 5).ReceiveViaInbox(new object(), Context("msg-1"), () =>
            {
                handlerRuns++;
                return Task.CompletedTask;
            });

            handlerRuns.Should().Be(0, "a not-yet-visible 404 that converges to a genuine marker is a confirmed duplicate");
            container.Verify(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2), "the read-back retries once past the not-yet-visible 404");
            clock.Delays.Should().HaveCount(1, "exactly one backoff separated the two read attempts");
        }

        [Fact]
        public async Task MustRedeliverWhenReadBackBudgetIsExhaustedByNotFound()
        {
            var handlerRuns = 0;
            var clock = new Clock();
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Conflict));
            SetupRead(container, () => Response(HttpStatusCode.NotFound));

            Func<Task> act = () => InboxOver(container.Object, clock, readBackMaxAttempts: 3).ReceiveViaInbox(new object(), Context("msg-1"), () =>
            {
                handlerRuns++;
                return Task.CompletedTask;
            });

            await act.Should().ThrowAsync<InvalidOperationException>("an exhausted read-back budget cannot confirm the duplicate — redeliver, never skip");
            handlerRuns.Should().Be(0);
            container.Verify(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Exactly(3), "the read-back attempts exactly the configured budget before failing");
            clock.Delays.Should().HaveCount(2, "a 3-attempt budget backs off between attempts (not after the final one)");
        }

        [Fact]
        public async Task MustRunHandlerExactlyOnceWhenTwoConcurrentDeliveriesRaceOnTheSameId()
        {
            var handlerRuns = 0;
            var container = new Mock<Container>();
            // One create wins (201); the other loses (409) and confirms the genuine marker the winner wrote.
            container.SetupSequence(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Response(HttpStatusCode.Created))
                     .ReturnsAsync(Response(HttpStatusCode.Conflict));
            SetupRead(container, () => Response(HttpStatusCode.OK, GenuineMarkerPayload("msg-1")));
            var inbox = InboxOver(container.Object, new Clock());

            await inbox.ReceiveViaInbox(new object(), Context("msg-1"), () => { handlerRuns++; return Task.CompletedTask; });
            await inbox.ReceiveViaInbox(new object(), Context("msg-1"), () => { handlerRuns++; return Task.CompletedTask; });

            handlerRuns.Should().Be(1, "exactly one of two same-id deliveries wins the claim and runs the handler");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustFailLoudWithoutRunningHandlerOrWritingWhenMessageIdIsMissing(string messageId)
        {
            var handlerRuns = 0;
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Created));

            Func<Task> act = () => InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context(messageId), () =>
            {
                handlerRuns++;
                return Task.CompletedTask;
            });

            await act.Should().ThrowAsync<InvalidOperationException>("a null/whitespace message id fails loud (ADR-0009 D2)");
            handlerRuns.Should().Be(0, "the handler must never run for an unidentifiable message");
            container.Verify(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never, "nothing is written for a missing id");
        }

        [Fact]
        public async Task MustCompensationDeleteMarkerAndRethrowWhenHandlerThrowsAfterAFreshClaim()
        {
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Created));
            container.Setup(c => c.DeleteItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Response(HttpStatusCode.NoContent));

            Func<Task> act = () => InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context("msg-1"), () =>
                throw new InvalidOperationException("handler-boom"));

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("handler-boom",
                "the ORIGINAL handler exception is rethrown after compensation");
            container.Verify(c => c.DeleteItemStreamAsync(CosmosItemId.ForInbox("msg-1"), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once, "the fresh claim is compensation-deleted so a redelivery can re-claim");
        }

        [Fact]
        public async Task MustSwallowCompensationDeleteFailureButStillRethrowTheOriginalHandlerException()
        {
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Created));
            container.Setup(c => c.DeleteItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new Exception("delete-failed"));

            Func<Task> act = () => InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context("msg-1"), () =>
                throw new InvalidOperationException("handler-boom"));

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("handler-boom",
                "a failed compensation-delete is swallowed so the original handler exception still drives redelivery");
        }

        [Fact]
        public async Task MustThrowNotSupportedFromHasBeenReceivedBecauseDedupIsTheWriteAheadClaimNotARead()
        {
            IInboxDeduplicator inbox = InboxOver(new Mock<Container>().Object, new Clock());

            Func<Task> act = () => inbox.HasBeenReceived("msg-1", CancellationToken.None);

            await act.Should().ThrowAsync<NotSupportedException>();
        }

        [Fact]
        public async Task MustFailLoudWhenTheMessageIdEncodesPastTheCosmosItemIdLimit()
        {
            var handlerRuns = 0;
            var container = new Mock<Container>();
            SetupCreate(container, () => Response(HttpStatusCode.Created));

            // A message id long enough to push the encoded inbox:{...} id over Cosmos's 1023-char id limit is rejected by
            // the shared id builder before any write, inheriting CosmosItemId's length guard.
            Func<Task> act = () => InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context(new string('x', 1000)), () =>
            {
                handlerRuns++;
                return Task.CompletedTask;
            });

            await act.Should().ThrowAsync<ArgumentException>();
            handlerRuns.Should().Be(0);
            container.Verify(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task MustStampDeterministicMarkerIdAndPartitionValueOnTheClaim()
        {
            string capturedJson = null;
            PartitionKey capturedPartitionKey = default;
            var container = new Mock<Container>();
            container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Callback<Stream, PartitionKey, ItemRequestOptions, CancellationToken>((payload, partitionKey, _, __) =>
                     {
                         capturedPartitionKey = partitionKey;
                         payload.Position = 0;
                         using var reader = new StreamReader(payload, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                         capturedJson = reader.ReadToEnd();
                     })
                     .ReturnsAsync(Response(HttpStatusCode.Created));

            await InboxOver(container.Object, new Clock()).ReceiveViaInbox(new object(), Context("msg-1"), () => Task.CompletedTask);

            using JsonDocument document = JsonDocument.Parse(capturedJson);
            JsonElement root = document.RootElement;
            root.GetProperty("id").GetString().Should().Be(CosmosItemId.ForInbox("msg-1"), "the marker id is the deterministic inbox:{encoded(messageId)}");
            root.GetProperty("_chatterType").GetString().Should().Be(CosmosItemId.InboxKind);
            root.GetProperty("MessageId").GetString().Should().Be("msg-1", "the raw message id is stored verbatim");
            root.GetProperty("idempotencyKey").GetString().Should().Be("msg-1", "the partition value is stamped at the container's /idempotencyKey path");
            capturedPartitionKey.Should().Be(new PartitionKey("msg-1"), "the claim targets the message-id partition");
        }

        [Fact]
        public async Task MustStampTtlOnlyWhenConfiguredAndOmitItWhenUnset()
        {
            string ttlConfiguredJson = null;
            string ttlUnsetJson = null;

            var configured = new Mock<Container>();
            configured.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                      .Callback<Stream, PartitionKey, ItemRequestOptions, CancellationToken>((payload, _, __, ___) => ttlConfiguredJson = ReadAll(payload))
                      .ReturnsAsync(Response(HttpStatusCode.Created));
            await InboxOver(configured.Object, new Clock(), markerTimeToLive: 3600).ReceiveViaInbox(new object(), Context("msg-1"), () => Task.CompletedTask);

            var unset = new Mock<Container>();
            unset.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                 .Callback<Stream, PartitionKey, ItemRequestOptions, CancellationToken>((payload, _, __, ___) => ttlUnsetJson = ReadAll(payload))
                 .ReturnsAsync(Response(HttpStatusCode.Created));
            await InboxOver(unset.Object, new Clock(), markerTimeToLive: null).ReceiveViaInbox(new object(), Context("msg-1"), () => Task.CompletedTask);

            JsonDocument.Parse(ttlConfiguredJson).RootElement.GetProperty("ttl").GetInt32().Should().Be(3600, "a configured TTL is stamped at the Cosmos-reserved ttl field");
            JsonDocument.Parse(ttlUnsetJson).RootElement.TryGetProperty("ttl", out _).Should().BeFalse("an unset TTL emits no ttl field (byte-identical to the document-tier marker)");
        }

        private static string ReadAll(Stream payload)
        {
            payload.Position = 0;
            using var reader = new StreamReader(payload, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            return reader.ReadToEnd();
        }
    }
}
