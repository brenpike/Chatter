using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using System;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingGuidIdGenerator
{
    public class WhenGeneratingId : Testing.Core.Context
    {
        private readonly GuidIdGenerator _sut = new GuidIdGenerator();

        [Fact]
        public void MustReturnNonEmptyGuid()
            => _sut.GenerateId().Should().NotBe(Guid.Empty);

        [Fact]
        public void MustReturnDistinctGuidsAcrossCalls()
            => _sut.GenerateId().Should().NotBe(_sut.GenerateId());

        [Fact]
        public void MustIgnoreSeedData()
        {
            var seed = Encoding.UTF8.GetBytes("seed");
            _sut.GenerateId(seed).Should().NotBe(Guid.Empty);
        }
    }
}
