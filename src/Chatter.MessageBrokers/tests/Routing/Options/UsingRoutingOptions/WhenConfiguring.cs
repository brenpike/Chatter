using Chatter.MessageBrokers.Routing.Options;
using FluentAssertions;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Options.UsingRoutingOptions
{
    public class WhenConfiguring : Testing.Core.Context
    {
        [Fact]
        public void MustReturnDefaultContentTypeWhenUnset()
        {
            var options = new SendOptions();

            options.ContentType.Should().Be("application/json");
            options.ContentType.Should().Be(RoutingOptions.DefaultContentType);
        }

        [Fact]
        public void MustRoundTripContentTypeThroughMessageContextWhenSet()
        {
            var options = new SendOptions();

            options.ContentType = "application/xml";

            options.ContentType.Should().Be("application/xml");
            options.MessageContext[MessageContext.ContentType].Should().Be("application/xml");
        }

        [Fact]
        public void MustWriteCorrelationIdToMessageContext()
        {
            var options = new SendOptions();

            options.SetCorrelationId("corr-123");

            options.MessageContext[MessageContext.CorrelationId].Should().Be("corr-123");
        }

        [Fact]
        public void MustWriteInfrastructureTypeToMessageContext()
        {
            var options = new SendOptions();

            options.UseMessagingInfrastructure(t => t.Default);

            options.MessageContext[MessageContext.InfrastructureType].Should().Be(string.Empty);
        }

        [Fact]
        public void MustReturnSameInstanceWhenMergeContextIsNull()
        {
            var options = new SendOptions();

            var result = options.Merge((IDictionary<string, object>)null);

            result.Should().BeSameAs(options);
        }

        [Fact]
        public void MustOverwriteAndCopyKeysWhenMergePopulatedContext()
        {
            var options = new SendOptions();
            options.SetCorrelationId("original");
            var contextToMerge = new Dictionary<string, object>
            {
                [MessageContext.CorrelationId] = "overwritten",
                [MessageContext.Subject] = "added"
            };

            options.Merge(contextToMerge);

            options.MessageContext[MessageContext.CorrelationId].Should().Be("overwritten");
            options.MessageContext[MessageContext.Subject].Should().Be("added");
        }
    }
}
