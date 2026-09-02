using Chatter.SqlChangeFeed.Scripts.Sql;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.Sql.UsingSqlIdentifier
{
    public class WhenEscaping : Testing.Core.Context
    {
        [Fact]
        public void MustWrapIdentifierInBrackets()
            => SqlIdentifier.Escape("MyQueue").Should().Be("[MyQueue]");

        [Fact]
        public void MustDoubleClosingBracketWithinIdentifier()
            => SqlIdentifier.Escape("My]Queue").Should().Be("[My]]Queue]");

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenIdentifierIsNullOrWhitespace(string name)
            => FluentActions.Invoking(() => SqlIdentifier.Escape(name))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustJoinSchemaAndNameAsSeparatelyEscapedParts()
            => SqlIdentifier.EscapeQualified("dbo", "MyQueue").Should().Be("[dbo].[MyQueue]");

        [Fact]
        public void MustEscapeClosingBracketInBothQualifiedParts()
            => SqlIdentifier.EscapeQualified("d]bo", "My]Queue").Should().Be("[d]]bo].[My]]Queue]");

        [Theory]
        [InlineData(null, "MyQueue")]
        [InlineData("   ", "MyQueue")]
        [InlineData("dbo", "")]
        [InlineData("dbo", null)]
        public void MustThrowWhenEitherQualifiedPartIsNullOrWhitespace(string schema, string name)
            => FluentActions.Invoking(() => SqlIdentifier.EscapeQualified(schema, name))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustTreatDotAsPartSeparatorWhenEscapingDottedName()
            => SqlIdentifier.EscapeQualified("dbo.MyQueue").Should().Be("[dbo].[MyQueue]");

        [Fact]
        public void MustEscapeUndottedNameAsSinglePart()
            => SqlIdentifier.EscapeQualified("MyQueue").Should().Be("[MyQueue]");

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("dbo.")]
        [InlineData(".MyQueue")]
        [InlineData("dbo..MyQueue")]
        public void MustThrowWhenDottedNameHasNullOrWhitespacePart(string dottedName)
            => FluentActions.Invoking(() => SqlIdentifier.EscapeQualified(dottedName))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustDoubleApostropheInLiteralByDefault()
            => SqlIdentifier.QuoteLiteral("O'Hara").Should().Be("O''Hara");

        [Fact]
        public void MustQuadrupleApostropheInLiteralNestedTwoLevelsDeep()
            => SqlIdentifier.QuoteLiteral("O'Hara", 2).Should().Be("O''''Hara");

        [Fact]
        public void MustDoubleEveryApostropheInLiteralWithManyApostrophes()
            => SqlIdentifier.QuoteLiteral("'a'b'").Should().Be("''a''b''");

        [Fact]
        public void MustQuadrupleEveryApostropheInLiteralWithManyApostrophes()
            => SqlIdentifier.QuoteLiteral("'a'b'", 2).Should().Be("''''a''''b''''");

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void MustThrowWhenLiteralNestingDepthIsBelowOne(int nestingDepth)
            => FluentActions.Invoking(() => SqlIdentifier.QuoteLiteral("O'Hara", nestingDepth))
                .Should().Throw<ArgumentOutOfRangeException>();

        [Fact]
        public void MustThrowWhenLiteralValueIsNull()
            => FluentActions.Invoking(() => SqlIdentifier.QuoteLiteral(null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustKeepApostropheIntactWithinBracketedIdentifier()
            => SqlIdentifier.Escape("My'Queue").Should().Be("[My'Queue]");

        [Fact]
        public void MustContainStatementTerminatorAndCommentWithinBracketedIdentifier()
            => SqlIdentifier.Escape("MyQueue];--").Should().Be("[MyQueue]];--]");

        [Fact]
        public void MustEscapeBracketPrefixedIdentifierRatherThanPassItThrough()
            => SqlIdentifier.Escape("[evil").Should().Be("[[evil]");

        [Fact]
        public void MustEscapeAlreadyBracketedIdentifierRatherThanPassItThrough()
            => SqlIdentifier.Escape("[evil]").Should().Be("[[evil]]]");

        [Fact]
        public void MustTreatDotAsPartOfNameWhenEscapingSingleIdentifier()
            => SqlIdentifier.Escape("dbo.MyQueue").Should().Be("[dbo.MyQueue]");
    }
}
