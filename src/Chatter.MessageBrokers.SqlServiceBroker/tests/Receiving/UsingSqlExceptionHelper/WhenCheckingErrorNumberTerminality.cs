using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingSqlExceptionHelper
{
    public class WhenCheckingErrorNumberTerminality : Testing.Core.Context
    {
        [Theory]
        [InlineData(102)]
        [InlineData(208)]
        public void MustReturnTrueForDeterministicConfigurationErrorNumber(int errorNumber)
            => SqlExceptionHelper.IsErrorNumberTerminal(errorNumber).Should().BeTrue();

        [Theory]
        [InlineData(1205)]
        [InlineData(10054)]
        [InlineData(40613)]
        [InlineData(49920)]
        public void MustReturnFalseForTransientErrorNumber(int errorNumber)
            => SqlExceptionHelper.IsErrorNumberTerminal(errorNumber).Should().BeFalse();

        [Theory]
        [InlineData(0)]
        [InlineData(-2)]
        [InlineData(50000)]
        [InlineData(int.MaxValue)]
        public void MustReturnFalseForAnUnclassifiedErrorNumber(int errorNumber)
            => SqlExceptionHelper.IsErrorNumberTerminal(errorNumber).Should().BeFalse();

        [Theory]
        [InlineData(102)]
        [InlineData(208)]
        public void MustNeverClassifyATerminalErrorNumberAsTransient(int errorNumber)
        {
            SqlExceptionHelper.IsErrorNumberTransient(errorNumber).Should().BeFalse();
            SqlExceptionHelper.IsErrorNumberTerminal(errorNumber).Should().BeTrue();
        }
    }
}
