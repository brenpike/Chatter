using Chatter.MessageBrokers.AzureServiceBus.Receiving.Retry;
using FluentAssertions;
using Microsoft.Azure.ServiceBus;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.Retry.UsingServiceBusRetryExceptionPredicatesProvider
{
    // ServiceBusRetryExceptionPredicatesProvider is internal sealed (IVT covers it).
    // Each predicate matches its exception type only when IsTransient == true.
    // NOTE: ServiceBusCommunicationException, ServerBusyException, and ServiceBusTimeoutException
    // hardcode IsTransient == true with no public ctor/setter to make them non-transient, so the
    // "no match when non-transient" assertion can only be pinned for ServiceBusException.
    public class WhenProvidingPredicates : Testing.Core.Context
    {
        private readonly Predicate<Exception>[] _predicates =
            new ServiceBusRetryExceptionPredicatesProvider().GetExceptionPredicates().ToArray();

        [Fact]
        public void MustProvideExactlyFourPredicates()
            => _predicates.Should().HaveCount(4);

        [Fact]
        public void MustMatchTransientServiceBusException()
            => _predicates.Any(p => p(new ServiceBusException(isTransient: true, "m"))).Should().BeTrue();

        [Fact]
        public void MustNotMatchNonTransientServiceBusException()
            => _predicates.Any(p => p(new ServiceBusException(isTransient: false, "m"))).Should().BeFalse();

        [Fact]
        public void MustMatchTransientServiceBusCommunicationException()
            => _predicates.Any(p => p(new ServiceBusCommunicationException("m"))).Should().BeTrue();

        [Fact]
        public void MustMatchTransientServerBusyException()
            => _predicates.Any(p => p(new ServerBusyException("m"))).Should().BeTrue();

        [Fact]
        public void MustMatchTransientServiceBusTimeoutException()
            => _predicates.Any(p => p(new ServiceBusTimeoutException("m"))).Should().BeTrue();

        [Fact]
        public void MustNotMatchForeignException()
            => _predicates.Any(p => p(new InvalidOperationException("m"))).Should().BeFalse();
    }
}
