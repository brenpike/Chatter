using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingSettlementResult
{
    public class WhenConstructing : Testing.Core.Context
    {
        [Fact]
        public void MustReportASettledDeliveryAsSettled()
        {
            var sut = SettlementResult.Settled();
            sut.Outcome.Should().Be(SettlementOutcome.Settled);
            sut.IsSettled.Should().BeTrue();
        }

        [Fact]
        public void MustReportADeliveryThatNeedsNoSettlementAsNotRequired()
        {
            var sut = SettlementResult.NotRequired("receive and delete");
            sut.Outcome.Should().Be(SettlementOutcome.NotRequired);
            sut.IsSettled.Should().BeFalse();
            sut.Reason.Should().Be("receive and delete");
        }

        [Fact]
        public void MustReportAnAttemptedSettlementThatDidNotHappenAsFailed()
        {
            var sut = SettlementResult.Failed("the message could not be located");
            sut.Outcome.Should().Be(SettlementOutcome.Failed);
            sut.IsSettled.Should().BeFalse();
            sut.Reason.Should().Be("the message could not be located");
        }

        [Fact]
        public void MustNotReadTheDefaultValueAsSettled()
        {
            var sut = default(SettlementResult);
            sut.Outcome.Should().NotBe(SettlementOutcome.Settled);
            sut.IsSettled.Should().BeFalse();
        }

        [Fact]
        public void MustCarryAReasonOnTheDefaultValue()
            => default(SettlementResult).Reason.Should().NotBeNullOrWhiteSpace();

        [Fact]
        public void MustCarryNoReasonWhenSettled()
            => SettlementResult.Settled().Reason.Should().BeNullOrEmpty();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustCarryAReasonWhenAnUnsettledOutcomeIsGivenNone(string reason)
        {
            SettlementResult.NotRequired(reason).Reason.Should().NotBeNullOrWhiteSpace();
            SettlementResult.Failed(reason).Reason.Should().NotBeNullOrWhiteSpace();
        }
    }
}
