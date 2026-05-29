using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Context.UsingFailureContext
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();

        public WhenConstructing()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private InboundBrokeredMessage CreateInbound()
            => new InboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", _bodyConverter.Object);

        private FailureContext CreateSut(
            string failureDescription = "description",
            Exception failure = null,
            int deliveryCount = 1,
            TransactionContext transactionContext = null)
            => new FailureContext(
                CreateInbound(),
                "error-queue",
                failureDescription,
                failure ?? new InvalidOperationException("boom"),
                deliveryCount,
                transactionContext ?? new TransactionContext());

        [Fact]
        public void MustMapInbound()
            => CreateSut().Inbound.Should().NotBeNull();

        [Fact]
        public void MustMapErrorQueueName()
            => CreateSut().ErrorQueueName.Should().Be("error-queue");

        [Fact]
        public void MustMapFailureDescription()
            => CreateSut(failureDescription: "the-description").FailureDescription.Should().Be("the-description");

        [Fact]
        public void MustMapFailure()
        {
            var failure = new InvalidOperationException("specific");
            CreateSut(failure: failure).Failure.Should().BeSameAs(failure);
        }

        [Fact]
        public void MustMapDeliveryCount()
            => CreateSut(deliveryCount: 7).DeliveryCount.Should().Be(7);

        [Fact]
        public void MustMapTransactionContext()
        {
            var transactionContext = new TransactionContext("receiver");
            CreateSut(transactionContext: transactionContext).TransactionContext.Should().BeSameAs(transactionContext);
        }

        [Fact]
        public void MustExposeContextContainer()
            => CreateSut().Container.Should().NotBeNull();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowArgumentExceptionWhenFailureDescriptionIsNullOrWhitespace(string failureDescription)
            => FluentActions.Invoking(() => CreateSut(failureDescription: failureDescription))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustFormatToStringFromDescriptionAndFailure()
        {
            var failure = new InvalidOperationException("kaboom");
            var sut = CreateSut(failureDescription: "desc", failure: failure);
            sut.ToString().Should().StartWith("desc:");
            sut.ToString().Should().Contain("kaboom");
        }

        [Fact]
        public void MustThrowNullReferenceFromToStringWhenFailureIsNull()
        {
            // INVARIANT: the constructor accepts a null failure, but ToString() dereferences
            // Failure.Message, so calling it on a null-failure context throws NRE.
            var nullFailureSut = new FailureContext(CreateInbound(), "error-queue", "desc", null, 1, new TransactionContext());
            FluentActions.Invoking(() => nullFailureSut.ToString())
                .Should().Throw<NullReferenceException>();
        }
    }
}
