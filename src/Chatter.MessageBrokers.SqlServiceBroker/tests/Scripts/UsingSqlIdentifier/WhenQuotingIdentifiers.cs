using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Scripts.UsingSqlIdentifier
{
    public class WhenQuotingIdentifiers : Testing.Core.Context
    {
        [Fact]
        public void MustEscapeClosingBracketInRawIdentifier()
            => SqlIdentifier.Quote("q]; DROP TABLE x--").Should().Be("[q]]; DROP TABLE x--]");

        [Fact]
        public void MustPassThroughWellFormedQuotedIdentifier()
            => SqlIdentifier.Quote("[q]").Should().Be("[q]");

        [Fact]
        public void MustEscapeWholeValueWhenInteriorBracketIsNotDoubled()
            => SqlIdentifier.Quote("[x]; DROP TABLE t; --").Should().Be("[[x]]; DROP TABLE t; --]");

        // A trailing "]]" is identifier content, not a terminator, so a value whose only closing
        // bracket is that escaped pair is UNTERMINATED and must never be trusted as already-quoted.
        [Fact]
        public void MustEscapeWholeValueWhenTheFinalBracketIsAnEscapedPairRatherThanATerminator()
            => SqlIdentifier.Quote("[x]]").Should().Be("[[x]]]]]");

        [Fact]
        public void MustEscapeWholeValueWhenAnUnterminatedNameEndsInAnEscapedPair()
            => SqlIdentifier.Quote("[name]]").Should().Be("[[name]]]]]");

        [Fact]
        public void MustEscapeWholeValueWhenAPayloadFollowsAnEscapedPair()
            => SqlIdentifier.Quote("[x]];payload]]").Should().Be("[[x]]]];payload]]]]]");

        [Fact]
        public void MustEscapeAnUnterminatedEscapedPairPartOfAMultiPartName()
            => SqlIdentifier.QuoteMultiPart("[dbo].[x]]").Should().Be("[dbo].[[x]]]]]");

        [Fact]
        public void MustNotSplitOnDots()
            => SqlIdentifier.Quote("//company.com/service").Should().Be("[//company.com/service]");

        [Fact]
        public void MustRejectNullIdentifier()
            => FluentActions.Invoking(() => SqlIdentifier.Quote(null)).Should().Throw<ArgumentException>();

        [Fact]
        public void MustRejectEmptyIdentifier()
            => FluentActions.Invoking(() => SqlIdentifier.Quote("")).Should().Throw<ArgumentException>();

        [Fact]
        public void MustRejectWhitespaceOnlyIdentifier()
            => FluentActions.Invoking(() => SqlIdentifier.Quote("   ")).Should().Throw<ArgumentException>();

        [Fact]
        public void MustQuoteEachPartOfASchemaQualifiedName()
            => SqlIdentifier.QuoteMultiPart("dbo.MyQueue").Should().Be("[dbo].[MyQueue]");

        [Fact]
        public void MustTreatADotInsideBracketsAsLiteral()
            => SqlIdentifier.QuoteMultiPart("[my.queue]").Should().Be("[my.queue]");

        [Fact]
        public void MustKeepEscapedBracketInsideABracketedPart()
            => SqlIdentifier.QuoteMultiPart("[dbo].[My]]Queue]").Should().Be("[dbo].[My]]Queue]");

        [Fact]
        public void MustQuoteASinglePartName()
            => SqlIdentifier.QuoteMultiPart("TestQueue").Should().Be("[TestQueue]");

        [Fact]
        public void MustEscapeAnUnterminatedBracketAsOnePart()
            => SqlIdentifier.QuoteMultiPart("[dbo.MyQueue").Should().Be("[[dbo.MyQueue]");

        [Fact]
        public void MustRejectATrailingEmptyPart()
            => FluentActions.Invoking(() => SqlIdentifier.QuoteMultiPart("dbo.")).Should().Throw<ArgumentException>();

        [Fact]
        public void MustRejectALeadingEmptyPart()
            => FluentActions.Invoking(() => SqlIdentifier.QuoteMultiPart(".q")).Should().Throw<ArgumentException>();

        [Fact]
        public void MustRejectAnInteriorEmptyPart()
            => FluentActions.Invoking(() => SqlIdentifier.QuoteMultiPart("a..b")).Should().Throw<ArgumentException>();

        [Fact]
        public void MustRejectNullMultiPartName()
            => FluentActions.Invoking(() => SqlIdentifier.QuoteMultiPart(null)).Should().Throw<ArgumentException>();

        [Fact]
        public void MustRejectEmptyMultiPartName()
            => FluentActions.Invoking(() => SqlIdentifier.QuoteMultiPart("")).Should().Throw<ArgumentException>();

        [Fact]
        public void MustRejectWhitespaceOnlyMultiPartName()
            => FluentActions.Invoking(() => SqlIdentifier.QuoteMultiPart("   ")).Should().Throw<ArgumentException>();

        [Fact]
        public void MustStripBracketsFromAWellFormedQuotedIdentifier()
            => SqlIdentifier.Unquote("[Target]").Should().Be("Target");

        [Fact]
        public void MustReturnAnUnbracketedIdentifierVerbatim()
            => SqlIdentifier.Unquote("my]service").Should().Be("my]service");

        [Fact]
        public void MustReturnAnIdentifierNotEndingInABracketVerbatim()
            => SqlIdentifier.Unquote("[Target]Svc").Should().Be("[Target]Svc");

        [Fact]
        public void MustUndoubleEscapedBracketsWhenStripping()
            => SqlIdentifier.Unquote("[My]]Queue]").Should().Be("My]Queue");

        [Fact]
        public void MustReturnAMultiPartNameVerbatim()
            => SqlIdentifier.Unquote("[dbo].[MyQueue]").Should().Be("[dbo].[MyQueue]");

        [Fact]
        public void MustReturnEmptyBracketsAsAnEmptyIdentifier()
            => SqlIdentifier.Unquote("[]").Should().Be("");

        [Fact]
        public void MustReturnNullVerbatimWithoutThrowing()
            => SqlIdentifier.Unquote(null).Should().BeNull();

        [Fact]
        public void MustReturnEmptyVerbatimWithoutThrowing()
            => SqlIdentifier.Unquote("").Should().Be("");

        [Theory]
        [InlineData("q]; DROP TABLE x--")]
        [InlineData("[q]")]
        [InlineData("[x]; DROP TABLE t; --")]
        [InlineData("//company.com/service")]
        [InlineData("TestQueue")]
        [InlineData("dbo.MyQueue")]
        [InlineData("[my.queue]")]
        [InlineData("[dbo].[My]]Queue]")]
        [InlineData("[dbo.MyQueue")]
        [InlineData("My]Queue")]
        public void MustQuoteIdempotently(string identifier)
        {
            var quoted = SqlIdentifier.Quote(identifier);
            SqlIdentifier.Quote(quoted).Should().Be(quoted);
        }

        [Theory]
        [InlineData("q]; DROP TABLE x--")]
        [InlineData("[q]")]
        [InlineData("[x]; DROP TABLE t; --")]
        [InlineData("//company.com/service")]
        [InlineData("TestQueue")]
        [InlineData("dbo.MyQueue")]
        [InlineData("[my.queue]")]
        [InlineData("[dbo].[My]]Queue]")]
        [InlineData("[dbo.MyQueue")]
        [InlineData("My]Queue")]
        public void MustQuoteMultiPartIdempotently(string identifier)
        {
            var quoted = SqlIdentifier.QuoteMultiPart(identifier);
            SqlIdentifier.QuoteMultiPart(quoted).Should().Be(quoted);
        }

        [Theory]
        [InlineData("q]; DROP TABLE x--")]
        [InlineData("[q]")]
        [InlineData("[x]; DROP TABLE t; --")]
        [InlineData("//company.com/service")]
        [InlineData("TestQueue")]
        [InlineData("My]Queue")]
        public void MustUnquoteBackToTheRawIdentifier(string identifier)
            => SqlIdentifier.Unquote(SqlIdentifier.Quote(identifier)).Should().Be(SqlIdentifier.Unquote(identifier));
    }
}
