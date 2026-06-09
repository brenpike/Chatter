using System;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Routing.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Scheduled-delivery coverage driven THROUGH Chatter. The SYSTEM UNDER TEST is Chatter's outbound
    // scheduled-enqueue path: a command is dispatched via IBrokeredMessageDispatcher.Send with the
    // ASBMessageContext.ScheduledEnqueueTimeUtc header set on SendOptions. Chatter's
    // OutboundBrokeredMessageExtensions.AsAzureServiceBusMessage reads that header and stamps
    // ServiceBusMessage.ScheduledEnqueueTime, so the broker holds the message until the scheduled time before
    // delivering it to Chatter's pump and the RecordingMessageHandler.
    //
    // The proof is purely through Chatter: the handler is NOT invoked before the scheduled time (a short-timeout
    // WaitForHandledAsync times out) and IS invoked after it (a second wait with margin succeeds). Timings are
    // tolerant for the slow emulator.
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineScheduledDeliveryTests
    {
        private const string ScheduledQueue = "chatter.roundtrip";
        // How far in the future the message is scheduled. Long enough that the "not yet" assertion is meaningful
        // against the slow emulator, short enough to keep the test quick.
        private static readonly TimeSpan ScheduleDelay = TimeSpan.FromSeconds(10);
        // A wait that must EXPIRE before the scheduled time elapses — kept comfortably under ScheduleDelay so a
        // premature delivery (the failure this guards) would be observed.
        private static readonly TimeSpan BeforeScheduleWait = TimeSpan.FromSeconds(4);
        // A wait that starts at/after the scheduled time, with generous margin for the emulator to deliver.
        private static readonly TimeSpan AfterScheduleWait = TimeSpan.FromSeconds(45);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineScheduledDeliveryTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        public sealed class ScheduledCommand : ICommand
        {
            public string Value { get; set; }
        }

        // A command dispatched with a future ScheduledEnqueueTimeUtc is held by the broker until that time:
        // Chatter's pump does NOT deliver it before the scheduled time (the bounded wait times out), then DOES
        // deliver it after, proving Chatter's scheduled-enqueue header reached the broker and was honored.
        [RequiresDockerFact]
        public async Task ScheduledCommandIsNotDeliveredBeforeItsScheduledTimeThenIsDeliveredAfter()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<ScheduledCommand>(ScheduledQueue),
                typeof(ScheduledCommand));
            await harness.StartAsync();

            // Chatter's scheduled-enqueue is set via the ASB-specific header on SendOptions; the outbound mapper
            // (AsAzureServiceBusMessage) reads ASBMessageContext.ScheduledEnqueueTimeUtc and stamps
            // ServiceBusMessage.ScheduledEnqueueTime. The value must be a DateTime (GetScheduledEnqueueTimeUtc
            // casts to DateTime?).
            var scheduledTimeUtc = DateTime.UtcNow + ScheduleDelay;
            var options = new SendOptions();
            options.WithMessageContext(ASBMessageContext.ScheduledEnqueueTimeUtc, scheduledTimeUtc);

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new ScheduledCommand { Value = "scheduled" }, ScheduledQueue, options: options);
            }

            // Before the scheduled time: the handler must NOT have been invoked. A bounded wait that expires
            // before ScheduleDelay proves the broker is holding the message.
            Func<Task> prematureWait = () => harness.WaitForHandledAsync<ScheduledCommand>(BeforeScheduleWait);
            await prematureWait.Should().ThrowAsync<TimeoutException>(
                "a scheduled message must not be delivered before its ScheduledEnqueueTimeUtc");

            // After the scheduled time: the handler IS invoked. The wait margin covers the remaining schedule
            // delay plus the emulator's delivery latency.
            var handled = await harness.WaitForHandledAsync<ScheduledCommand>(AfterScheduleWait);
            handled.Message.Should().NotBeNull(
                "the scheduled message must be delivered once its ScheduledEnqueueTimeUtc has elapsed");
            handled.Message.Value.Should().Be("scheduled");
        }
    }
}
