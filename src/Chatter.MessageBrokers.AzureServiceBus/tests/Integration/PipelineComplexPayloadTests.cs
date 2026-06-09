using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // End-to-end coverage of Chatter's System.Text.Json body path against a real Azure Service Bus round trip,
    // guarding the Newtonsoft -> STJ migration where it matters most: NON-flat payloads (the other pipeline
    // tests cover only flat scalars). The SYSTEM UNDER TEST is Chatter's send + receive serialization:
    //
    //   Test A: a command carrying a nested object, a List<T>, a Dictionary<string,object>, a by-name enum, a
    //     bool and a numeric field is dispatched ONLY through Chatter's IBrokeredMessageDispatcher.Send,
    //     delivered by Chatter's pump to a Chatter-resolved RecordingMessageHandler, and EVERY field is
    //     asserted to round-trip exactly. This proves the STJ Convert (send) and GetMessageFromBody (receive)
    //     agree on nested/collection/enum/dictionary shapes over the wire, not just flat scalars.
    //
    //   Test B (read-leniency): a RAW message body with QUOTED scalars ({"Flag":"true","Count":"3"}) is
    //     injected via a raw-SDK edge SEND — Chatter's own send never emits quoted scalars, so a raw edge-send
    //     is the only way to exercise the Newtonsoft read-leniency the converters restore (this mirrors the
    //     existing edge-only SDK usage in PipelineDeadLetterTests/PipelineTransactionModeTests). A Chatter
    //     receiver + handler then reads it, and the assertion is that Chatter's lenient deserialization coerced
    //     the quoted "true" to a CLR bool and the quoted "3" to a CLR int.
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineComplexPayloadTests
    {
        private const string ComplexQueue = "chatter.roundtrip";
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineComplexPayloadTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        public enum PayloadKind
        {
            Unspecified = 0,
            Booked = 1,
            Cancelled = 2,
        }

        public sealed class NestedDetail
        {
            public string Label { get; set; }
            public int Score { get; set; }
        }

        // A command whose shape spans every non-flat STJ path the migration must preserve: a nested object, a
        // List<T>, a Dictionary<string,object>, a by-name enum, a bool and a numeric field. Each is asserted
        // independently after the round trip.
        public sealed class ComplexCommand : ICommand
        {
            public NestedDetail Nested { get; set; }
            public List<string> Tags { get; set; }
            public Dictionary<string, object> Attributes { get; set; }
            public PayloadKind Kind { get; set; }
            public bool Enabled { get; set; }
            public decimal Amount { get; set; }
        }

        // The DTO the raw-SDK edge-send targets. Flag/Count are strongly-typed bool/int so the QUOTED scalars in
        // the raw body exercise Chatter's read-leniency converters (NewtonsoftLenientBooleanConverter +
        // AllowReadingFromString) rather than the strict STJ defaults.
        public sealed class LenientScalarCommand : ICommand
        {
            public bool Flag { get; set; }
            public int Count { get; set; }
        }

        // Test A: a non-flat payload sent through Chatter's dispatcher is delivered by Chatter's pump to the
        // handler with every member — nested object, list, dictionary, by-name enum, bool, numeric — restored
        // exactly. Asserts purely on the payload Chatter handed the handler, proving the STJ body path survives a
        // real Azure Service Bus round trip for non-scalar shapes.
        [RequiresDockerFact]
        public async Task ComplexPayloadRoundTripsEveryFieldThroughChatterSerializer()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<ComplexCommand>(ComplexQueue),
                typeof(ComplexCommand));
            await harness.StartAsync();

            var sent = new ComplexCommand
            {
                Nested = new NestedDetail { Label = "inner", Score = 99 },
                Tags = new List<string> { "first", "second", "third" },
                Attributes = new Dictionary<string, object>
                {
                    ["region"] = "us-east",
                    ["priority"] = 7L,
                    ["urgent"] = true,
                },
                Kind = PayloadKind.Cancelled,
                Enabled = true,
                Amount = 123.45m,
            };

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(sent, ComplexQueue);
            }

            var handled = await harness.WaitForHandledAsync<ComplexCommand>(HandlerWait);

            handled.Message.Should().NotBeNull("Chatter's pipeline must deliver the complex command to the handler");

            // Nested object survives verbatim.
            handled.Message.Nested.Should().NotBeNull("a nested object must round-trip through STJ");
            handled.Message.Nested.Label.Should().Be("inner");
            handled.Message.Nested.Score.Should().Be(99);

            // List<T> preserves contents AND order.
            handled.Message.Tags.Should().Equal("first", "second", "third");

            // Dictionary<string,object> values materialize back to CLR types (string/long/bool) — the untyped
            // read path Chatter's MaterializingObjectConverter restores.
            handled.Message.Attributes.Should().ContainKey("region").WhoseValue.Should().Be("us-east");
            handled.Message.Attributes.Should().ContainKey("priority").WhoseValue.Should().Be(7L);
            handled.Message.Attributes.Should().ContainKey("urgent").WhoseValue.Should().Be(true);

            // Enum round-trips to the exact value.
            handled.Message.Kind.Should().Be(PayloadKind.Cancelled);

            // Bool and numeric fields survive exactly.
            handled.Message.Enabled.Should().BeTrue();
            handled.Message.Amount.Should().Be(123.45m);
        }

        // Edge-only raw-SDK send: writes a JSON body with QUOTED scalars directly to the queue, the one form
        // Chatter's own send never emits. Mirrors the existing edge-only SDK usage in the deadletter/transaction
        // tests — the SDK appears solely to seed the body, never as the system under test.
        private async Task RawSendQuotedScalarBodyAsync(string queue, string jsonBody)
        {
            await using var client = new ServiceBusClient(_emulator.GetConnectionString());
            var sender = client.CreateSender(queue);
            var message = new ServiceBusMessage(BinaryData.FromBytes(Encoding.UTF8.GetBytes(jsonBody)))
            {
                ContentType = "application/json",
            };
            await sender.SendMessageAsync(message);
        }

        // Test B: a raw message body with QUOTED scalars ({"Flag":"true","Count":"3"}) is read by a Chatter
        // receiver + handler; Chatter's lenient deserialization coerces Flag to the CLR bool true and Count to
        // the CLR int 3. Proves the read-leniency converters (restoring Newtonsoft parity) work across a real
        // Azure Service Bus receive, not just in isolation.
        [RequiresDockerFact]
        public async Task QuotedScalarBodyIsCoercedByChatterLenientDeserialization()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<LenientScalarCommand>(ComplexQueue),
                typeof(LenientScalarCommand));
            await harness.StartAsync();

            // PascalCase member names so the body binds regardless of case-insensitivity, with the scalars
            // QUOTED to exercise read-leniency. Chatter's send would emit `"Flag":true,"Count":3`.
            await RawSendQuotedScalarBodyAsync(ComplexQueue, "{\"Flag\":\"true\",\"Count\":\"3\"}");

            var handled = await harness.WaitForHandledAsync<LenientScalarCommand>(HandlerWait);

            handled.Message.Should().NotBeNull("Chatter must deserialize the raw quoted-scalar body and deliver it");
            handled.Message.Flag.Should().BeTrue(
                "Chatter's NewtonsoftLenientBooleanConverter coerces a quoted \"true\" to the CLR bool true");
            handled.Message.Count.Should().Be(
                3,
                "Chatter's AllowReadingFromString coerces a quoted \"3\" to the CLR int 3");
        }
    }
}
