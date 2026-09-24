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

        // INVARIANT: the provider yields exactly two predicates — the driver-IsTransient-minus-terminal
        // predicate and the package's own IsErrorNumberTransient predicate. Pinned by this fact:
        // deleting either `yield return` in SqlRetryExceptionPredicatesProvider reddens it and
        // nothing else in the SqlServiceBroker unit suite (observed) (ADR-0027).
        [Fact]
        public void MustYieldBothSqlClassificationPredicates()
            => _sut.GetExceptionPredicates().Should().HaveCount(2);

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
        // (e is SqlException && IsTransient && !IsErrorNumberTerminal(Number) / IsErrorNumberTransient(Number))
        // are NOT directly pinnable here. SqlException is sealed with no public constructor and
        // cannot be mocked by Moq or instantiated without a live SQL connection, so only the
        // non-SqlException and null branches plus the predicate count are pinned.
    }
}
