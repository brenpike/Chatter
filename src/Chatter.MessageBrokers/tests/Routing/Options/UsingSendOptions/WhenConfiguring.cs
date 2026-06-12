using Chatter.MessageBrokers.Routing.Options;
using FluentAssertions;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Options.UsingSendOptions
{
    public class WhenConfiguring : Testing.Core.Context
    {
        [Fact]
        public void MustWriteSubjectToMessageContext()
        {
            var options = new SendOptions();

            options.WithSubject("the-subject");

            options.MessageContext[MessageContext.Subject].Should().Be("the-subject");
        }

        [Fact]
        public void MustWriteGroupIdToMessageContext()
        {
            var options = new SendOptions();

            options.WithGroupId("group-1");

            options.MessageContext[MessageContext.GroupId].Should().Be("group-1");
        }

        [Fact]
        public void MustWriteTimeToLiveAsTimeSpanFromMinutesToMessageContext()
        {
            var options = new SendOptions();

            options.WithTimeToLiveInMinutes(5);

            options.MessageContext[MessageContext.TimeToLive].Should().Be(TimeSpan.FromMinutes(5));
        }

        [Fact]
        public void MustWriteReplyToAddressToMessageContext()
        {
            var options = new SendOptions();

            options.SetReplyToAddress("reply-here");

            options.MessageContext[MessageContext.ReplyToAddress].Should().Be("reply-here");
        }

        [Fact]
        public void MustWriteReplyToGroupIdToMessageContext()
        {
            var options = new SendOptions();

            options.SetReplyToGroupId("reply-group");

            options.MessageContext[MessageContext.ReplyToGroupId].Should().Be("reply-group");
        }

        [Fact]
        public void MustReturnSendOptionsWhenMergeOptionsToMergeIsNull()
        {
            var options = new SendOptions();

            var result = options.Merge((SendOptions)null);

            result.Should().BeSameAs(options);
        }

        [Fact]
        public void MustMergeContextWhenMergePopulatedSendOptions()
        {
            var options = new SendOptions();
            options.WithSubject("original");
            var optionsToMerge = new SendOptions();
            optionsToMerge.WithSubject("overwritten").WithGroupId("added");

            var result = options.Merge(optionsToMerge);

            result.MessageContext[MessageContext.Subject].Should().Be("overwritten");
            result.MessageContext[MessageContext.GroupId].Should().Be("added");
        }

        [Fact]
        public void MustProduceSendOptionsOverSuppliedContextWhenCreateUsed()
        {
            var context = new Dictionary<string, object>
            {
                [MessageContext.Subject] = "supplied"
            };

            var options = SendOptions.Create(context);

            options.Should().BeOfType<SendOptions>();
            options.MessageContext[MessageContext.Subject].Should().Be("supplied");
        }
    }
}
