using Chatter.MessageBrokers.SqlServiceBroker.Receiving.Retry;
using FluentAssertions;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.Retry.UsingSqlRetryExceptionPredicatesProvider
{
    public class WhenGettingExceptionPredicates : Testing.Core.Context
    {
        private readonly SqlRetryExceptionPredicatesProvider _sut = new SqlRetryExceptionPredicatesProvider();

        // INVARIANT: the `#if NET5_0_OR_GREATER` block in the provider adds the
        // `exception.IsTransient` predicate only on net5.0+, so the count is 3 there and 2 on
        // netcoreapp3.1. The #if below mirrors the production directive exactly.
        [Fact]
        public void MustYieldExpectedNumberOfPredicatesForTargetFramework()
#if NET5_0_OR_GREATER
            => _sut.GetExceptionPredicates().Should().HaveCount(3);
#else
            => _sut.GetExceptionPredicates().Should().HaveCount(2);
#endif

        [Fact]
        public void MustReturnFalseFromEveryPredicateForNonSqlException()
        {
            var nonSqlException = new InvalidOperationException();

            _sut.GetExceptionPredicates()
                .Select(predicate => predicate(nonSqlException))
                .Should().OnlyContain(result => result == false);
        }

        [Fact]
        public void MustReturnFalseFromEveryPredicateForNull()
        {
            _sut.GetExceptionPredicates()
                .Select(predicate => predicate(null))
                .Should().OnlyContain(result => result == false);
        }

        // CHARACTERIZATION BOUNDARY: the SqlException-positive branches of each predicate
        // (e is SqlException && IsTransient / IsErrorNumberTransient(Number) / Number == 208)
        // are NOT directly pinnable here. SqlException is sealed with no public constructor and
        // cannot be mocked by Moq or instantiated without a live SQL connection, so only the
        // non-SqlException and null branches plus the predicate count are pinned.
    }
}
