using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingSqlExceptionHelper
{
    public class WhenCheckingErrorNumberTransience : Testing.Core.Context
    {
        [Theory]
        [InlineData(49920)]
        [InlineData(49919)]
        [InlineData(49918)]
        [InlineData(41839)]
        [InlineData(41325)]
        [InlineData(41305)]
        [InlineData(41302)]
        [InlineData(41301)]
        [InlineData(40613)]
        [InlineData(40501)]
        [InlineData(40197)]
        [InlineData(20041)]
        [InlineData(17197)]
        [InlineData(14355)]
        [InlineData(10936)]
        [InlineData(10929)]
        [InlineData(10928)]
        [InlineData(10922)]
        [InlineData(10060)]
        [InlineData(10054)]
        [InlineData(10053)]
        [InlineData(9515)]
        [InlineData(8651)]
        [InlineData(8645)]
        [InlineData(8628)]
        [InlineData(4221)]
        [InlineData(4060)]
        [InlineData(3966)]
        [InlineData(3960)]
        [InlineData(3935)]
        [InlineData(1807)]
        [InlineData(1221)]
        [InlineData(1205)]
        [InlineData(1204)]
        [InlineData(1203)]
        [InlineData(997)]
        [InlineData(921)]
        [InlineData(669)]
        [InlineData(617)]
        [InlineData(601)]
        [InlineData(233)]
        [InlineData(121)]
        [InlineData(64)]
        [InlineData(20)]
        public void MustReturnTrueForDocumentedTransientErrorNumber(int errorNumber)
            => SqlExceptionHelper.IsErrorNumberTransient(errorNumber).Should().BeTrue();

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(100)]
        [InlineData(208)]
        [InlineData(50000)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void MustReturnFalseForNonTransientErrorNumber(int errorNumber)
            => SqlExceptionHelper.IsErrorNumberTransient(errorNumber).Should().BeFalse();

        // INVARIANT: error -2 is pinned NON-transient because the `case -2:` branch is commented out
        // in production (SqlExceptionHelper.cs); the source comment notes -2 can be thrown even on
        // successful completion, so it is deliberately treated as non-transient.
        [Fact]
        public void MustReturnFalseForMinusTwoBecauseItsCaseIsCommentedOut()
            => SqlExceptionHelper.IsErrorNumberTransient(-2).Should().BeFalse();
    }
}
