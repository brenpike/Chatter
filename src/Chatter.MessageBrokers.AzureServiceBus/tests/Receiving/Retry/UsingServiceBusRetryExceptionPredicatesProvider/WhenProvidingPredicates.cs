using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Receiving.Retry;
using FluentAssertions;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.Retry.UsingServiceBusRetryExceptionPredicatesProvider
{
    // ServiceBusRetryExceptionPredicatesProvider is internal sealed (IVT covers it).
    // Azure.Messaging.ServiceBus 7.20.1 collapses the legacy distinct exception subtypes into a single
    // ServiceBusException carrying a ServiceBusFailureReason; IsTransient is derived from that reason
    // (true only for ServiceCommunicationProblem/ServiceBusy/ServiceTimeout). The predicates classify
    // the same transient/retryable cases the legacy subtype predicates did.
    public class WhenProvidingPredicates : Testing.Core.Context
    {
        private readonly Predicate<Exception>[] _predicates =
            new ServiceBusRetryExceptionPredicatesProvider().GetExceptionPredicates().ToArray();

        [Fact]
        public void MustProvideExactlyFourPredicates()
            => _predicates.Should().HaveCount(4);

        [Fact]
        public void MustMatchTransientServiceBusException()
            => _predicates.Any(p => p(new ServiceBusException("m", ServiceBusFailureReason.ServiceBusy))).Should().BeTrue();

        [Fact]
        public void MustNotMatchNonTransientServiceBusException()
            => _predicates.Any(p => p(new ServiceBusException("m", ServiceBusFailureReason.GeneralError))).Should().BeFalse();

        [Fact]
        public void MustMatchCommunicationProblemServiceBusException()
            => _predicates.Any(p => p(new ServiceBusException("m", ServiceBusFailureReason.ServiceCommunicationProblem))).Should().BeTrue();

        [Fact]
        public void MustMatchServiceBusyServiceBusException()
            => _predicates.Any(p => p(new ServiceBusException("m", ServiceBusFailureReason.ServiceBusy))).Should().BeTrue();

        [Fact]
        public void MustMatchServiceTimeoutServiceBusException()
            => _predicates.Any(p => p(new ServiceBusException("m", ServiceBusFailureReason.ServiceTimeout))).Should().BeTrue();

        [Fact]
        public void MustNotMatchForeignException()
            => _predicates.Any(p => p(new InvalidOperationException("m"))).Should().BeFalse();
    }
}
