using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingOutboundBrokeredMessage
{
    public class WhenAccessingMessageContext : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly byte[] _body = new byte[] { 1, 2, 3 };

        public WhenAccessingMessageContext()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private OutboundBrokeredMessage CreateSut(IDictionary<string, object> messageContext = null)
            => new OutboundBrokeredMessage("message-id", _body, messageContext ?? new Dictionary<string, object>(), "destination", _bodyConverter.Object);

        [Fact]
        public void MustStringifyBodyUsingBodyConverter()
        {
            _bodyConverter.Setup(c => c.Stringify(_body)).Returns("stringified");
            CreateSut().Stringify().Should().Be("stringified");
            _bodyConverter.Verify(c => c.Stringify(_body), Times.Once);
        }

        [Fact]
        public void MustStampTimeToLive()
        {
            var sut = CreateSut();
            var ttl = TimeSpan.FromMinutes(5);
            sut.WithTimeToLive(ttl);
            sut.MessageContext[MessageContext.TimeToLive].Should().Be(ttl);
        }

        [Fact]
        public void MustReturnTimeToLiveWhenStoredAsTimeSpan()
        {
            var sut = CreateSut();
            var ttl = TimeSpan.FromMinutes(3);
            sut.WithTimeToLive(ttl);
            sut.GetTimeToLive().Should().Be(ttl);
        }

        [Fact]
        public void MustReturnNullTimeToLiveWhenMissing()
            => CreateSut().GetTimeToLive().Should().BeNull();

        [Fact]
        public void MustParseTimeToLiveWhenStoredAsString()
        {
            var context = new Dictionary<string, object> { [MessageContext.TimeToLive] = "00:02:00" };
            CreateSut(context).GetTimeToLive().Should().Be(TimeSpan.FromMinutes(2));
        }

        [Fact]
        public void MustOverwriteCorrelationId()
        {
            var sut = CreateSut();
            sut.WithCorrelationId("new-corr");
            sut.CorrelationId.Should().Be("new-corr");
        }

        [Fact]
        public void MustReadInfrastructureTypeFromContext()
        {
            var context = new Dictionary<string, object> { [MessageContext.InfrastructureType] = "infra" };
            CreateSut(context).InfrastructureType.Should().Be("infra");
        }

        [Fact]
        public void MustReturnNullInfrastructureTypeWhenMissing()
            => CreateSut().InfrastructureType.Should().BeNull();

        [Fact]
        public void MustReadReceiveAttemptsFromContext()
        {
            var context = new Dictionary<string, object> { [MessageContext.ReceiveAttempts] = 4 };
            CreateSut(context).ReceiveAttempts.Should().Be(4);
        }

        [Fact]
        public void MustReturnZeroReceiveAttemptsWhenMissing()
            => CreateSut().ReceiveAttempts.Should().Be(0);

        [Fact]
        public void MustReturnDefaultWhenTypedKeyMissing()
            => CreateSut().GetMessageContextByKey<string>("missing").Should().BeNull();

        [Fact]
        public void MustReturnNullObjectWhenUntypedKeyMissing()
            => CreateSut().GetMessageContextByKey("missing").Should().BeNull();

        [Fact]
        public void MustSetTimeToLiveFromPositiveExpiryDuration()
        {
            var context = new Dictionary<string, object>
            {
                [MessageContext.ExpiryTimeUtc] = DateTime.UtcNow.AddHours(1)
            };
            var sut = CreateSut(context);
            sut.RefreshTimeToLive();
            sut.GetTimeToLive().Should().NotBeNull();
            sut.GetTimeToLive().Value.Should().BeGreaterThan(TimeSpan.Zero);
        }

        [Fact]
        public void MustSetNegativeTimeToLiveWhenExpiryHasPassed()
        {
            // INVARIANT: RefreshTimeToLive gates on ttl.Duration() (absolute value), which is
            // positive for an already-passed expiry, so the raw NEGATIVE ttl is stored rather
            // than TimeSpan.Zero. The zero branch is only reached for an exactly-now expiry.
            var context = new Dictionary<string, object>
            {
                [MessageContext.ExpiryTimeUtc] = DateTime.UtcNow.AddHours(-1)
            };
            var sut = CreateSut(context);
            sut.RefreshTimeToLive();
            sut.GetTimeToLive().Should().NotBeNull();
            sut.GetTimeToLive().Value.Should().BeLessThan(TimeSpan.Zero);
        }

        [Fact]
        public void MustLeaveTimeToLiveUnchangedWhenNoExpirySet()
        {
            var sut = CreateSut();
            sut.RefreshTimeToLive();
            sut.GetTimeToLive().Should().BeNull();
        }

        [Fact]
        public void MustReadAMismatchedKindAsAbsentFromTheTypedAccessor()
        {
            var context = new Dictionary<string, object> { ["key"] = 42L };
            CreateSut(context).GetMessageContextByKey<int>("key").Should().Be(0);
        }

        [Fact]
        public void MustReportAMismatchedKindAsNotFoundFromTheTryAccessor()
        {
            var context = new Dictionary<string, object> { ["key"] = 42L };
            CreateSut(context).TryGetMessageContextByKey<int>("key", out _).Should().BeFalse();
        }

        [Fact]
        public void MustReportAPresentMatchingKindFromTheTryAccessor()
        {
            var context = new Dictionary<string, object> { ["key"] = 42L };
            var found = CreateSut(context).TryGetMessageContextByKey<long>("key", out var value);
            (found, value).Should().Be((true, 42L));
        }

        [Fact]
        public void MustReportAnAbsentKeyAsNotFoundFromTheTryAccessor()
            => CreateSut().TryGetMessageContextByKey<string>("missing", out _).Should().BeFalse();

        [Fact]
        public void MustReadInfrastructureTypeAsNullWhenPersistedKindIsNotAString()
        {
            var context = new Dictionary<string, object> { [MessageContext.InfrastructureType] = 42L };
            CreateSut(context).InfrastructureType.Should().BeNull();
        }

        [Fact]
        public void MustStampAFreshCorrelationIdWhenThePersistedCorrelationIdIsNotAString()
        {
            var context = new Dictionary<string, object> { [MessageContext.CorrelationId] = 42L };
            Guid.TryParse(CreateSut(context).CorrelationId, out _).Should().BeTrue();
        }

        [Fact]
        public void MustLeaveTimeToLiveAbsentWhenPersistedKindIsNeitherTimeSpanNorString()
        {
            var context = new Dictionary<string, object> { [MessageContext.TimeToLive] = 120L };
            CreateSut(context).GetTimeToLive().Should().BeNull();
        }

        [Fact]
        public void MustLeaveTimeToLiveAbsentWhenPersistedStringIsNotAParsableDuration()
        {
            var context = new Dictionary<string, object> { [MessageContext.TimeToLive] = "not-a-duration" };
            CreateSut(context).GetTimeToLive().Should().BeNull();
        }

        [Fact]
        public void MustLeaveTimeToLiveUnchangedWhenExpiryKindIsNotADateTime()
        {
            var ttl = TimeSpan.FromMinutes(7);
            var context = new Dictionary<string, object> { [MessageContext.ExpiryTimeUtc] = "not-a-date" };
            var sut = CreateSut(context).WithTimeToLive(ttl);
            sut.RefreshTimeToLive();
            sut.GetTimeToLive().Should().Be(ttl);
        }

        [Fact]
        public void MustReadReceiveAttemptsAsZeroWhenPersistedKindIsNotConvertible()
        {
            var context = new Dictionary<string, object> { [MessageContext.ReceiveAttempts] = Guid.NewGuid() };
            CreateSut(context).ReceiveAttempts.Should().Be(0);
        }

        [Fact]
        public void MustReadReceiveAttemptsAsZeroWhenPersistedStringIsNotNumeric()
        {
            var context = new Dictionary<string, object> { [MessageContext.ReceiveAttempts] = "not-a-number" };
            CreateSut(context).ReceiveAttempts.Should().Be(0);
        }

        [Fact]
        public void MustReadReceiveAttemptsAsZeroWhenPersistedNumberOverflowsAnInt()
        {
            var context = new Dictionary<string, object> { [MessageContext.ReceiveAttempts] = long.MaxValue };
            CreateSut(context).ReceiveAttempts.Should().Be(0);
        }
    }
}
