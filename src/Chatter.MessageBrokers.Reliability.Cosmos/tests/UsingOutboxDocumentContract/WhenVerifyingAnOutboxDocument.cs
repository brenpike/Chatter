using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingOutboxDocumentContract
{
    public class WhenVerifyingAnOutboxDocument : Testing.Core.Context
    {
        private const string MessageId = "msg-1";

        // A wire-faithful admitted outbox document carrying exactly the fields the relay's reconstruction reads. Each
        // parameter lets ONE field diverge so a test isolates a single violation; a null parameter OMITS the property
        // entirely (the absent-field cases). Exotic shapes a JsonObject cannot express as a string field — a non-string
        // Destination, a non-object document — are built through Parse instead.
        private static JsonElement Document(
            string destination = "dest",
            string messageBody = "{}",
            string messageContentType = "application/json",
            string messageContext = "{}")
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = CosmosItemId.ForOutbox(MessageId),
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = MessageId,
            };
            AddWhenPresent(node, CosmosOutboxDocument.DestinationField, destination);
            AddWhenPresent(node, CosmosOutboxDocument.MessageBodyField, messageBody);
            AddWhenPresent(node, CosmosOutboxDocument.MessageContentTypeField, messageContentType);
            AddWhenPresent(node, CosmosOutboxDocument.MessageContextField, messageContext);
            return Parse(node.ToJsonString());
        }

        private static void AddWhenPresent(JsonObject node, string propertyName, string value)
        {
            if (value is not null)
            {
                node[propertyName] = value;
            }
        }

        // A persisted MessageContext carrying one entry, serialized the way the outbox persists it: a JSON string field
        // whose content is the serialized context object.
        private static string SerializedContext(string key, JsonNode value)
            => new JsonObject { [key] = value }.ToJsonString();

        // The default document with ONE field replaced by a JSON number — the non-string shape the string-valued builder
        // above cannot express, and the shape the contract must read as absent rather than fault on.
        private static JsonElement DocumentWithNonStringField(string propertyName)
        {
            JsonObject node = JsonNode.Parse(Document().GetRawText()).AsObject();
            node[propertyName] = 7;
            return Parse(node.ToJsonString());
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        [Fact]
        public void MustVerifyADocumentCarryingEveryFieldTheReconstructionReads()
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document());

            verification.IsSatisfied.Should().BeTrue("every field the reconstruction reads is present and well formed");
            verification.Violations.Should().BeEmpty();
            verification.ViolationMessage.Should().BeNull();
            verification.Destination.Should().Be("dest");
            verification.MessageBody.Should().Be("{}");
            verification.ContentType.Should().Be("application/json");
            verification.MessageContext.Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public void MustCarryTheMaterializedMessageContextOnTheVerifiedDescriptor()
        {
            JsonElement document = Document(messageContext: SerializedContext("tenant", "acme"));

            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(document);

            verification.IsSatisfied.Should().BeTrue();
            verification.MessageContext.Should().ContainKey("tenant").WhoseValue.Should().Be("acme");
        }

        [Theory]
        [InlineData(null)]  // the document carries no content-type field at all
        [InlineData("")]    // an empty content type resolves nothing
        [InlineData("   ")] // a whitespace content type resolves nothing
        public void MustResolveTheContentTypeFromTheMessageContextWhenTheDocumentCarriesNone(string documentContentType)
        {
            JsonElement document = Document(
                messageContentType: documentContentType,
                messageContext: SerializedContext(MessageContext.ContentType, "application/json"));

            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(document);

            verification.IsSatisfied.Should().BeTrue("the persisted message context supplies the content type the document lacks");
            verification.ContentType.Should().Be("application/json");
        }

        [Theory]
        [InlineData(null)]  // absent
        [InlineData("")]    // empty
        [InlineData("   ")] // whitespace
        public void MustNameABlankOrAbsentDestination(string destination)
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(destination: destination));

            verification.IsSatisfied.Should().BeFalse("OutboundBrokeredMessage's constructor throws for a blank destination");
            verification.Violations.Should().ContainSingle()
                        .Which.Should().Contain(CosmosOutboxDocument.DestinationField);
        }

        [Fact]
        public void MustNameANonStringDestination()
        {
            JsonElement document = DocumentWithNonStringField(CosmosOutboxDocument.DestinationField);

            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(document);

            verification.IsSatisfied.Should().BeFalse("a non-string destination is read as absent and cannot reach the constructor");
            verification.Violations.Should().ContainSingle()
                        .Which.Should().Contain(CosmosOutboxDocument.DestinationField);
        }

        [Fact]
        public void MustNameAnAbsentMessageBody()
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(messageBody: null));

            verification.IsSatisfied.Should().BeFalse("the body converter throws when handed no body string");
            verification.Violations.Should().ContainSingle()
                        .Which.Should().Contain(CosmosOutboxDocument.MessageBodyField);
        }

        [Fact]
        public void MustNameANonStringMessageBody()
        {
            JsonElement document = DocumentWithNonStringField(CosmosOutboxDocument.MessageBodyField);

            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(document);

            verification.IsSatisfied.Should().BeFalse("a non-string body is read as absent and yields no body bytes");
            verification.Violations.Should().ContainSingle()
                        .Which.Should().Contain(CosmosOutboxDocument.MessageBodyField);
        }

        [Fact]
        public void MustAcceptAnEmptyMessageBody()
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(messageBody: string.Empty));

            verification.IsSatisfied.Should().BeTrue("an empty body string converts to an empty byte array, which publishes");
            verification.MessageBody.Should().BeEmpty();
        }

        [Theory]
        [InlineData("not json")] // not JSON at all
        [InlineData("[1,2]")]    // JSON, but not an object the context deserializes from
        [InlineData("5")]        // JSON, but not an object the context deserializes from
        public void MustNameAMessageContextThatWillNotMaterialize(string serializedMessageContext)
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(messageContext: serializedMessageContext));

            verification.IsSatisfied.Should().BeFalse("materializing the persisted context throws for this document, every time");
            verification.Violations.Should().ContainSingle()
                        .Which.Should().Contain(CosmosOutboxDocument.MessageContextField);
        }

        [Fact]
        public void MustNameAMessageContextThatMaterializesToNothing()
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(messageContext: "null"));

            verification.IsSatisfied.Should().BeFalse("a context that materializes to null cannot be read for the content-type fallback");
            verification.Violations.Should().ContainSingle()
                        .Which.Should().Contain(CosmosOutboxDocument.MessageContextField);
        }

        [Theory]
        [InlineData(null)]  // absent
        [InlineData("")]    // empty
        [InlineData("   ")] // whitespace
        public void MustAcceptAnAbsentOrBlankMessageContext(string serializedMessageContext)
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(messageContext: serializedMessageContext));

            verification.IsSatisfied.Should().BeTrue("an absent or blank persisted context materializes to an empty context, exactly as the drain already reads it");
            verification.MessageContext.Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public void MustNameAContentTypeResolvableFromNeitherTheDocumentNorTheContext()
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(messageContentType: null));

            verification.IsSatisfied.Should().BeFalse("no content type means the body can never be serialized for publish");
            verification.Violations.Should().ContainSingle()
                        .Which.Should().Contain(CosmosOutboxDocument.MessageContentTypeField);
        }

        [Fact]
        public void MustNameANonStringContextContentTypeAsUnresolvable()
        {
            JsonElement document = Document(
                messageContentType: null,
                messageContext: SerializedContext(MessageContext.ContentType, 7));

            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(document);

            verification.IsSatisfied.Should().BeFalse("a non-string context content type resolves no content type");
            verification.Violations.Should().ContainSingle()
                        .Which.Should().Contain(CosmosOutboxDocument.MessageContentTypeField);
        }

        [Fact]
        public void MustNotNameAContentTypeViolationWhenTheDocumentCarriesOneAndTheContextIsUnusable()
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(messageContext: "not json"));

            verification.Violations.Should().ContainSingle("the document's own content type resolves without the context")
                        .Which.Should().Contain(CosmosOutboxDocument.MessageContextField);
        }

        [Fact]
        public void MustNameEveryViolationInOneFailure()
        {
            JsonElement document = Document(
                destination: null,
                messageBody: null,
                messageContentType: null,
                messageContext: "not json");

            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(document);

            verification.IsSatisfied.Should().BeFalse();
            verification.Violations.Should().HaveCount(4, "one evaluation names every violation so a fix-restart-rediscover loop is impossible");
            verification.ViolationMessage.Should()
                        .Contain(CosmosOutboxDocument.DestinationField)
                        .And.Contain(CosmosOutboxDocument.MessageBodyField)
                        .And.Contain(CosmosOutboxDocument.MessageContentTypeField)
                        .And.Contain(CosmosOutboxDocument.MessageContextField);
        }

        [Fact]
        public void MustNameTheOutboxDocumentInItsViolationMessage()
        {
            OutboxDocumentVerification verification = OutboxDocumentContract.Verify(Document(destination: null));

            verification.ViolationMessage.Should().Contain(MessageId);
        }

        [Theory]
        [InlineData("null")]         // a JSON null document
        [InlineData("5")]            // a JSON number document
        [InlineData("[]")]           // a JSON array document
        [InlineData("\"text\"")]     // a JSON string document
        [InlineData("{}")]           // an empty object carrying none of the fields
        [InlineData("{\"Destination\":{\"nested\":true}}")] // an object whose fields are the wrong shape
        public void MustNeverThrowForAnyDocumentShape(string json)
        {
            Func<OutboxDocumentVerification> verify = () => OutboxDocumentContract.Verify(Parse(json));

            verify.Should().NotThrow("the contract is total — a document it cannot verify yields violations, never an exception");
            verify().IsSatisfied.Should().BeFalse();
        }

        [Fact]
        public void MustNeverThrowForAnUndefinedDocument()
        {
            Func<OutboxDocumentVerification> verify = () => OutboxDocumentContract.Verify(default);

            verify.Should().NotThrow("an undefined JsonElement is an input like any other");
            verify().IsSatisfied.Should().BeFalse();
        }

        [Fact]
        public void MustNameTheSameViolationsWhenTheSameDocumentIsVerifiedAgain()
        {
            JsonElement document = Document(destination: null, messageContext: "not json");

            OutboxDocumentVerification first = OutboxDocumentContract.Verify(document);
            OutboxDocumentVerification second = OutboxDocumentContract.Verify(document);

            second.IsSatisfied.Should().Be(first.IsSatisfied);
            second.Violations.Should().Equal(first.Violations,
                "the document is immutable and the verification is pure, so a second evaluation is provably identical — there is nothing for a retry counter to learn");
        }
    }
}
