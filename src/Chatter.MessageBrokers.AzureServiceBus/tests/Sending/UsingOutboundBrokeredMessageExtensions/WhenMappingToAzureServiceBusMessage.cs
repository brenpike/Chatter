using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingOutboundBrokeredMessageExtensions
{
    // OutboundBrokeredMessage is constructed via its public ctor with a real JsonBodyConverter
    // (not a mock). The public ctor stamps ContentType and auto-stamps a CorrelationId when blank.
    public class WhenMappingToAzureServiceBusMessage : Testing.Core.Context
    {
        private readonly byte[] _body = new byte[] { 1, 2, 3 };
        private readonly JsonBodyConverter _converter = new JsonBodyConverter();

        private OutboundBrokeredMessage CreateSut(string messageId = "message-id", IDictionary<string, object> context = null)
            => new OutboundBrokeredMessage(messageId, _body, context ?? new Dictionary<string, object>(), "destination", _converter);

        [Fact]
        public void MustUseSuppliedMessageId()
            => CreateSut().AsAzureServiceBusMessage().MessageId.Should().Be("message-id");

        [Fact]
        public void MustGenerateNonEmptyMessageIdWhenBlank()
            => CreateSut(messageId: "").AsAzureServiceBusMessage().MessageId.Should().NotBeNullOrEmpty();

        [Fact]
        public void MustMapBody()
            => CreateSut().AsAzureServiceBusMessage().Body.Should().Equal(_body);

        [Fact]
        public void MustMapContentTypeFromConverter()
            => CreateSut().AsAzureServiceBusMessage().ContentType.Should().Be("application/json");

        [Fact]
        public void MustStampNonEmptyCorrelationId()
            => CreateSut().AsAzureServiceBusMessage().CorrelationId.Should().NotBeNullOrEmpty();

        [Fact]
        public void MustMapLabelFromSubject()
        {
            var context = new Dictionary<string, object> { [MessageContext.Subject] = "the-subject" };
            CreateSut(context: context).AsAzureServiceBusMessage().Label.Should().Be("the-subject");
        }

        [Fact]
        public void MustThrowWhenGroupIdSetWithoutMatchingPartitionKey()
        {
            // INVARIANT: the mapping assigns SessionId then PartitionKey on the SDK Message; for a
            // partitioned message the SDK requires PartitionKey == SessionId, so a GroupId without a
            // matching PartitionKey makes set_PartitionKey throw InvalidOperationException.
            var context = new Dictionary<string, object> { [MessageContext.GroupId] = "group-1" };
            Action map = () => CreateSut(context: context).AsAzureServiceBusMessage();
            map.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void MustMapSessionIdFromGroupIdWhenPartitionKeyMatches()
        {
            var sut = CreateSut().WithPartitionKey("group-1");
            sut.MessageContext[MessageContext.GroupId] = "group-1";
            var message = sut.AsAzureServiceBusMessage();
            message.SessionId.Should().Be("group-1");
            message.PartitionKey.Should().Be("group-1");
        }

        [Fact]
        public void MustMapReplyToFromReplyToAddress()
        {
            var context = new Dictionary<string, object> { [MessageContext.ReplyToAddress] = "reply-here" };
            CreateSut(context: context).AsAzureServiceBusMessage().ReplyTo.Should().Be("reply-here");
        }

        [Fact]
        public void MustMapReplyToSessionIdFromReplyToGroupId()
        {
            var context = new Dictionary<string, object> { [MessageContext.ReplyToGroupId] = "reply-group" };
            CreateSut(context: context).AsAzureServiceBusMessage().ReplyToSessionId.Should().Be("reply-group");
        }

        [Fact]
        public void MustMapPartitionKey()
        {
            var sut = CreateSut().WithPartitionKey("pk");
            sut.AsAzureServiceBusMessage().PartitionKey.Should().Be("pk");
        }

        [Fact]
        public void MustMapViaPartitionKey()
        {
            var sut = CreateSut().WithViaPartitionKey("via-pk");
            sut.AsAzureServiceBusMessage().ViaPartitionKey.Should().Be("via-pk");
        }

        [Fact]
        public void MustMapTo()
        {
            var sut = CreateSut().WithTo("the-to");
            sut.AsAzureServiceBusMessage().To.Should().Be("the-to");
        }

        [Fact]
        public void MustCopyTimeToLiveWhenSet()
        {
            var sut = CreateSut();
            sut.WithTimeToLive(TimeSpan.FromMinutes(7));
            sut.AsAzureServiceBusMessage().TimeToLive.Should().Be(TimeSpan.FromMinutes(7));
        }

        [Fact]
        public void MustCopyScheduledEnqueueTimeUtcWhenSet()
        {
            var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var sut = CreateSut().WithScheduledEnqueueTimeUtc(when);
            sut.AsAzureServiceBusMessage().ScheduledEnqueueTimeUtc.Should().Be(when);
        }

        [Fact]
        public void MustLeaveScheduledEnqueueTimeUtcAtDefaultWhenAbsent()
            => CreateSut().AsAzureServiceBusMessage().ScheduledEnqueueTimeUtc.Should().Be(default(DateTime));
    }
}
