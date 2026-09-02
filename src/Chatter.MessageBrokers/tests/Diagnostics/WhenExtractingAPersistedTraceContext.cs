using Chatter.MessageBrokers.Diagnostics;
using FluentAssertions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// Reading the trace context a WRITER persisted, back off a carrier typed as
    /// <see cref="IDictionary{TKey, TValue}"/> — the shape both <c>OutboundBrokeredMessage.MessageContext</c> and the
    /// relay's materialised context are declared as.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenExtractingAPersistedTraceContext : Testing.Core.Context
    {
        // The W3C Trace Context specification's own example traceparent, standing in for the value a writer
        // persisted alongside the message.
        private const string PersistedTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        private const string PersistedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        private const string PersistedSpanId = "00f067aa0ba902b7";

        [Fact]
        public void MustReadTheWriteTimeTraceContextOffTheMessageContext()
        {
            IDictionary<string, object> messageContext = new ConcurrentDictionary<string, object>();
            messageContext[TraceContextHeaders.TraceParent] = PersistedTraceParent;

            TraceContextPropagator.TryExtractFromMessageContext(messageContext, out var context).Should().BeTrue();

            context.TraceId.ToHexString().Should().Be(PersistedTraceId);
            context.SpanId.ToHexString().Should().Be(PersistedSpanId);
            context.TraceFlags.Should().Be(ActivityTraceFlags.Recorded);
            context.IsRemote.Should().BeTrue("the persisted context was recorded by another hop, and often another process");
        }

        [Theory]
        [InlineData(PersistedTraceParent)]
        [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00")]
        [InlineData("not-a-traceparent")]
        [InlineData("")]
        public void MustAgreeWithTheReadOnlyCarrierMemberGivenEquivalentInput(string persistedTraceParent)
        {
            var messageContext = new Dictionary<string, object> { [TraceContextHeaders.TraceParent] = persistedTraceParent };

            var readFromMessageContext = TraceContextPropagator.TryExtractFromMessageContext(messageContext, out var messageContextResult);
            var readFromReadOnlyMessageContext = TraceContextPropagator.TryExtract(messageContext, out var readOnlyResult);

            readFromMessageContext.Should().Be(readFromReadOnlyMessageContext, "the two carrier types differ only in how they are declared");
            messageContextResult.Should().Be(readOnlyResult);
        }

        [Fact]
        public void MustReadNoTraceContextWhenTheWriterPersistedNone()
        {
            var messageContext = new Dictionary<string, object> { ["chatter.unrelated-key"] = "unrelated-value" };

            TraceContextPropagator.TryExtractFromMessageContext(messageContext, out var context).Should().BeFalse();

            context.Should().Be(default(ActivityContext));
        }

        [Fact]
        public void MustReadNoTraceContextFromAnEmptyOrAbsentMessageContext()
        {
            TraceContextPropagator.TryExtractFromMessageContext(new Dictionary<string, object>(), out var emptyResult).Should().BeFalse();
            TraceContextPropagator.TryExtractFromMessageContext(null, out var absentResult).Should().BeFalse();

            emptyResult.Should().Be(default(ActivityContext));
            absentResult.Should().Be(default(ActivityContext));
        }

        [Fact]
        public void MustReadNoTraceContextWhenThePersistedTraceParentIsMalformed()
        {
            var messageContext = new Dictionary<string, object> { [TraceContextHeaders.TraceParent] = "00-not-a-trace-id-01" };

            TraceContextPropagator.TryExtractFromMessageContext(messageContext, out var context).Should().BeFalse("a poisoned header can never fail a delivery");

            context.Should().Be(default(ActivityContext));
        }

        /// <summary>
        /// The call-site shape that made a same-named overload impossible: a concrete
        /// <c>Dictionary&lt;string, object&gt;</c> satisfies BOTH <see cref="IDictionary{TKey, TValue}"/> and
        /// <see cref="IReadOnlyDictionary{TKey, TValue}"/>, so an overload pair would have failed to compile here with
        /// CS0121. That this test compiles at all IS the assertion.
        /// </summary>
        [Fact]
        public void MustBindAConcreteDictionaryWithoutAmbiguity()
        {
            var messageContext = new Dictionary<string, object> { [TraceContextHeaders.TraceParent] = PersistedTraceParent };

            TraceContextPropagator.TryExtractFromMessageContext(messageContext, out var context).Should().BeTrue();

            context.TraceId.ToHexString().Should().Be(PersistedTraceId);
        }

        /// <summary>
        /// A carrier that implements ONLY <see cref="IDictionary{TKey, TValue}"/>. Every carrier in the repository
        /// today is a <c>Dictionary</c> or a <c>ConcurrentDictionary</c>, both of which happen to satisfy
        /// <see cref="IReadOnlyDictionary{TKey, TValue}"/> as well — so nothing else pins the branch this member's
        /// own parameter type admits. <c>ExpandoObject</c> is used because the BCL already ships it as an
        /// <see cref="IDictionary{TKey, TValue}"/> that is not an <see cref="IReadOnlyDictionary{TKey, TValue}"/>.
        /// </summary>
        [Fact]
        public void MustReadTheTraceContextOffACarrierThatIsOnlyAnIDictionary()
        {
            IDictionary<string, object> messageContext = new ExpandoObject();
            messageContext[TraceContextHeaders.TraceParent] = PersistedTraceParent;

            messageContext.Should().NotBeAssignableTo<IReadOnlyDictionary<string, object>>();

            TraceContextPropagator.TryExtractFromMessageContext(messageContext, out var context).Should().BeTrue();

            context.TraceId.ToHexString().Should().Be(PersistedTraceId);
            context.SpanId.ToHexString().Should().Be(PersistedSpanId);
        }
    }
}
