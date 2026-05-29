using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using System;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingHashedBodyGuidGenerator
{
    public class WhenGeneratingId : Testing.Core.Context
    {
        private readonly HashedBodyGuidGenerator _sut = new HashedBodyGuidGenerator();

        [Fact]
        public void MustReturnNonEmptyGuidWhenSeedIsNull()
            => _sut.GenerateId(null).Should().NotBe(Guid.Empty);

        [Fact]
        public void MustReturnDistinctGuidsAcrossCallsWhenSeedIsNull()
            => _sut.GenerateId(null).Should().NotBe(_sut.GenerateId(null));

        [Fact]
        public void MustReturnDeterministicGuidForSameFixedSeed()
        {
            var seed = Encoding.UTF8.GetBytes("deterministic-seed");
            _sut.GenerateId(seed).Should().Be(_sut.GenerateId(seed));
        }

        [Fact]
        public void MustReturnDistinctGuidsForDifferingSeeds()
        {
            var firstSeed = Encoding.UTF8.GetBytes("first-seed");
            var secondSeed = Encoding.UTF8.GetBytes("second-seed");
            _sut.GenerateId(firstSeed).Should().NotBe(_sut.GenerateId(secondSeed));
        }

        [Fact]
        public void MustReturnDistinctGuidsForEmptySeedVersusNullSeed()
        {
            var emptySeed = new byte[0];
            _sut.GenerateId(emptySeed).Should().NotBe(_sut.GenerateId(null));
        }

        [Fact]
        public void MustReturnDeterministicGuidForEmptySeed()
        {
            _sut.GenerateId(new byte[0]).Should().Be(_sut.GenerateId(new byte[0]));
        }
    }
}
