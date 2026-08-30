using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Microsoft.Extensions.DependencyInjection;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Runs ADR-0010 D8 -- "no suppression of, and no key namespacing against, the broker SDKs' own
    // instrumentation" -- against the REAL Azure.Messaging.ServiceBus SDK on the emulator, across the three
    // configurations an application can be in: Chatter instrumented only, the SDK instrumented only, and both.
    //
    // The SDK is OFF by default; AzureSdkActivitySourceSwitch turns it on for this process from a
    // [ModuleInitializer] because the SDK caches the switch in a static constructor. Every configuration that
    // claims to observe the SDK therefore asserts that the SDK ACTUALLY EMITTED SPANS: a matrix that quietly
    // proves nothing because the SDK was never active would be worse than no matrix at all.
    //
    // "Instrumented" here means a .NET System.Diagnostics.ActivityListener -- the BCL subscription type, never
    // a Brokered Message Receiver -- is attached to the corresponding ActivitySource. That is the only knob:
    // Chatter's off-guard is its own source's HasListeners (ADR-0010 R1/R2) and the SDK creates its
    // ActivitySources only when the AppContext switch is on, then emits only when something subscribes.
    //
    // All emulator-backed facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is
    // absent so a plain `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs
    // them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class AzureServiceBusTraceContextInteropTests
    {
        // The ActivitySource names the SDK derives in DiagnosticScopeFactory.GetActivitySource as
        // "{clientNamespace}.{activityName up to its first dot}" -- so DiagnosticProperty.SendActivityName
        // ("ServiceBusSender.Send") lands on the sender source and MessageActivityName ("Message") on its own.
        private const string AzureSdkActivitySourcePrefix = "Azure.Messaging.ServiceBus";
        private const string AzureSdkSenderActivitySourceName = "Azure.Messaging.ServiceBus.ServiceBusSender";
        private const string AzureSdkMessageActivitySourceName = "Azure.Messaging.ServiceBus.Message";

        // MessagingClientDiagnostics.DiagnosticIdAttribute: the SDK's legacy correlation application property.
        private const string AzureSdkDiagnosticIdProperty = "Diagnostic-Id";

        private const string HostActivitySourceName = "Chatter.Tests.AzureServiceBusInterop";

        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SpanWait = TimeSpan.FromSeconds(15);

        // Stands in for the unrelated ambient instrumentation a real host always has (an ASP.NET Core request
        // activity, say). Chatter's send span nests inside it, so every assertion below is made under ambient
        // nesting rather than from a clean Activity.Current.
        private static readonly ActivitySource _hostActivitySource = new ActivitySource(HostActivitySourceName);

        private readonly ServiceBusEmulatorFixture _emulator;

        public AzureServiceBusTraceContextInteropTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        public sealed class InteropCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // INVARIANT: leased per TEST, never shared and never static, so no configuration in the matrix can
        // observe another configuration's stranded message. Leased inside the fact rather than in the
        // constructor so the non-Docker fact below consumes no queue from the pool.
        private string LeaseInteropQueue() => _emulator.LeaseQueue();

        private ChatterPipelineHarness BuildHarness(string queue)
            => ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<InteropCommand>(queue),
                typeof(InteropCommand));

        // Guards the whole matrix against vacuity, and needs no emulator: if the SDK's experimental switch is
        // not on before the SDK is first touched, every "the SDK emitted spans" assertion below would be
        // asserting against instrumentation that can never fire.
        [Fact]
        public void AzureSdkActivitySourceMustBeSwitchedOnForThisProcess()
            => AzureSdkActivitySourceSwitch.IsEnabled.Should().BeTrue(
                $"the [ModuleInitializer] in {nameof(AzureSdkActivitySourceSwitch)} must set the '{AzureSdkActivitySourceSwitch.SwitchName}' " +
                $"AppContext switch (env-var equivalent '{AzureSdkActivitySourceSwitch.EnvironmentVariableName}') before anything touches the " +
                "Azure SDK, which caches the value in a static constructor; without it the SDK creates no ActivitySource and this " +
                "interop matrix would silently prove nothing");

        // CHATTER ONLY. Chatter writes the trace context; the SDK, though switched on, has no subscriber and so
        // emits nothing. Proves Chatter's propagation does not depend on the SDK being instrumented.
        [RequiresDockerFact]
        public async Task ChatterOnlyPutsItsOwnTraceContextOnTheWireAndTheSdkEmitsNothing()
        {
            var queue = LeaseInteropQueue();
            using var recorder = SpanRecorder.Subscribing(toChatter: true, toAzureSdk: false);
            await using var harness = BuildHarness(queue);
            await harness.StartAsync();

            var handled = await DispatchUnderAmbientActivityAsync(harness, queue, "chatter-only");

            var sendSpan = recorder.RequireChatterSpan(BrokerDiagnostics.OperationTypes.Send);
            recorder.AzureSdkSpans.Should().BeEmpty(
                "with no .NET ActivityListener subscribed to the SDK's sources the SDK must emit nothing, even with its experimental switch on");

            var inbound = handled.Context.BrokeredMessage.MessageContext;
            inbound.Should().ContainKey(TraceContextHeaders.TraceParent);
            inbound[TraceContextHeaders.TraceParent].Should().Be(sendSpan.Id,
                "Chatter injects the send span's own W3C id and nothing on this path rewrites it");
            inbound.Should().NotContainKey(AzureSdkDiagnosticIdProperty);
        }

        // SDK ONLY. Chatter is off, so it writes no trace context, and the SDK's InstrumentMessage is free to
        // stamp its own legacy Diagnostic-Id. This is the CONTROL for the suppression pinned below: it shows
        // the SDK does stamp when Chatter leaves the message alone.
        [RequiresDockerFact]
        public async Task SdkOnlyStampsItsOwnDiagnosticIdWhenChatterWroteNoTraceContext()
        {
            var queue = LeaseInteropQueue();
            using var recorder = SpanRecorder.Subscribing(toChatter: false, toAzureSdk: true);
            await using var harness = BuildHarness(queue);
            await harness.StartAsync();

            var handled = await DispatchUnderAmbientActivityAsync(harness, queue, "sdk-only");

            recorder.AzureSdkSpans.Should().NotBeEmpty(
                "the SDK's own instrumentation must actually be running, otherwise this configuration proves nothing");
            recorder.SpansFrom(AzureSdkSenderActivitySourceName).Should().NotBeEmpty(
                "the SDK emits a send span for every dispatch it performs");
            recorder.SpansFrom(AzureSdkMessageActivitySourceName).Should().NotBeEmpty(
                "with no Chatter trace context on the message the SDK instruments the message itself");
            recorder.FindChatterSpan(BrokerDiagnostics.OperationTypes.Send).Should().BeNull(
                "Chatter's off-guard is its own source's listeners, so an application that instrumented only the SDK gets no Chatter span");

            var inbound = handled.Context.BrokeredMessage.MessageContext;
            inbound.Should().ContainKey(AzureSdkDiagnosticIdProperty,
                "the SDK stamps its legacy correlation property when the message carries neither Diagnostic-Id nor traceparent");
        }

        // BOTH. The load-bearing configuration: trace-id continuity from the ambient host activity through
        // Chatter's send span, the SDK's nested send span, the wire, and Chatter's receive span -- with the SDK
        // neither suppressed by Chatter nor overwriting Chatter's traceparent -- and the interop consequence
        // ADR-0010 records as inferred: the SDK's Diagnostic-Id stamping is suppressed because Chatter already
        // wrote traceparent.
        [RequiresDockerFact]
        public async Task BothInstrumentedKeepsOneTraceIdAndSuppressesTheSdkDiagnosticId()
        {
            var queue = LeaseInteropQueue();
            using var recorder = SpanRecorder.Subscribing(toChatter: true, toAzureSdk: true);
            await using var harness = BuildHarness(queue);
            await harness.StartAsync();

            var handled = await DispatchUnderAmbientActivityAsync(harness, queue, "both");

            var hostSpan = recorder.RequireSpanFrom(HostActivitySourceName);
            var chatterSendSpan = recorder.RequireChatterSpan(BrokerDiagnostics.OperationTypes.Send);
            var sdkSendSpan = recorder.RequireSpanFrom(AzureSdkSenderActivitySourceName);
            var chatterReceiveSpan = await recorder.WaitForChatterSpanAsync(BrokerDiagnostics.OperationTypes.Receive, SpanWait);

            recorder.AzureSdkSpans.Should().NotBeEmpty(
                "the SDK's own instrumentation must actually be running, otherwise this configuration proves nothing");
            chatterReceiveSpan.Should().NotBeNull($"Chatter must emit a receive span within {SpanWait}");

            // No suppression in either direction: both instrumentations produced spans for the same send, and
            // the SDK's span nests inside Chatter's rather than starting a trace of its own.
            sdkSendSpan.ParentSpanId.Should().Be(chatterSendSpan.SpanId,
                "Chatter spans are ordinary parents and the SDK's spans nest inside them (ADR-0010 D8)");

            // One trace id from the ambient host activity all the way to the far side of the broker boundary.
            chatterSendSpan.TraceId.Should().Be(hostSpan.TraceId, "Chatter's send span nests inside the ambient host activity");
            sdkSendSpan.TraceId.Should().Be(hostSpan.TraceId);
            chatterReceiveSpan.TraceId.Should().Be(hostSpan.TraceId, "the trace must survive the broker boundary");
            chatterReceiveSpan.ParentSpanId.Should().Be(chatterSendSpan.SpanId,
                "the extracted producer context is the PARENT of the receive span, never the ambient activity (ADR-0010 D6)");

            // Same-key last-writer-wins: both Chatter and the SDK are willing to write "traceparent", and the
            // value that survives is a value from this same trace. Here it is Chatter's, verbatim.
            var inbound = handled.Context.BrokeredMessage.MessageContext;
            inbound.Should().ContainKey(TraceContextHeaders.TraceParent);
            inbound[TraceContextHeaders.TraceParent].Should().Be(chatterSendSpan.Id);

            // THE INTEROP CONSEQUENCE. Azure's MessagingClientDiagnostics.InstrumentMessage short-circuits when
            // the message already carries "Diagnostic-Id" or "traceparent". Chatter wrote traceparent, so the SDK
            // stamps no Diagnostic-Id AND creates no per-message span -- while its send span is unaffected.
            // Contrast with SdkOnlyStampsItsOwnDiagnosticIdWhenChatterWroteNoTraceContext, where both appear.
            inbound.Should().NotContainKey(AzureSdkDiagnosticIdProperty,
                "the SDK skips its own instrumentation of a message that already carries a traceparent");
            recorder.SpansFrom(AzureSdkMessageActivitySourceName).Should().BeEmpty(
                "the same short-circuit that skips the Diagnostic-Id stamp also skips the SDK's per-message span");
        }

        // Dispatches one command through Chatter's dispatcher inside an ambient host activity and waits for
        // Chatter's pipeline to deliver it to the recording handler.
        private static async Task<HandledRecord<InteropCommand>> DispatchUnderAmbientActivityAsync(
            ChatterPipelineHarness harness,
            string queue,
            string marker)
        {
            using (var hostActivity = _hostActivitySource.StartActivity("host-work"))
            {
                hostActivity.Should().NotBeNull(
                    "the ambient host activity is the nesting this matrix asserts under; without it the test would assert nothing about nesting");

                var dispatcher = harness.CreateDispatcher(out var scope);
                using (scope)
                {
                    await dispatcher.Send(new InteropCommand { Marker = marker }, queue);
                }
            }

            return await harness.WaitForHandledAsync<InteropCommand>(HandlerWait);
        }

        // Collects stopped spans through a .NET System.Diagnostics.ActivityListener -- the BCL subscription
        // type, NOT a Brokered Message Receiver. Which sources it subscribes to IS the matrix dimension:
        // subscribing is what turns Chatter's instrumentation on (ADR-0010 R1) and what makes the SDK emit.
        private sealed class SpanRecorder : IDisposable
        {
            private readonly ConcurrentQueue<Activity> _stoppedSpans = new ConcurrentQueue<Activity>();
            private readonly ActivityListener _listener;
            private readonly bool _subscribeToChatter;
            private readonly bool _subscribeToAzureSdk;

            private SpanRecorder(bool subscribeToChatter, bool subscribeToAzureSdk)
            {
                _subscribeToChatter = subscribeToChatter;
                _subscribeToAzureSdk = subscribeToAzureSdk;

                _listener = new ActivityListener
                {
                    ShouldListenTo = ShouldSubscribeTo,
                    Sample = SampleAllData,
                    SampleUsingParentId = SampleAllDataUsingParentId,
                    ActivityStopped = _stoppedSpans.Enqueue,
                };

                ActivitySource.AddActivityListener(_listener);
            }

            // The two flags ARE the matrix. The "Chatter only" configuration proves the SDK stayed silent by not
            // subscribing to it at all, rather than by subscribing and hoping nothing arrives.
            public static SpanRecorder Subscribing(bool toChatter, bool toAzureSdk)
                => new SpanRecorder(toChatter, toAzureSdk);

            public IReadOnlyList<Activity> AzureSdkSpans
                => _stoppedSpans.Where(IsAzureSdkSpan).ToArray();

            public IReadOnlyList<Activity> SpansFrom(string activitySourceName)
                => _stoppedSpans.Where(span => span.Source.Name == activitySourceName).ToArray();

            public Activity FindChatterSpan(string operationType)
                => _stoppedSpans.FirstOrDefault(span =>
                    span.Source.Name == BrokerDiagnostics.ActivitySourceName
                    && (string)span.GetTagItem(BrokerDiagnostics.OperationType) == operationType);

            public Activity RequireChatterSpan(string operationType)
            {
                var span = FindChatterSpan(operationType);
                span.Should().NotBeNull($"Chatter must emit a '{operationType}' span when a .NET ActivityListener is attached to '{BrokerDiagnostics.ActivitySourceName}'");
                return span;
            }

            public Activity RequireSpanFrom(string activitySourceName)
            {
                var span = SpansFrom(activitySourceName).FirstOrDefault();
                span.Should().NotBeNull($"a span from ActivitySource '{activitySourceName}' is required for this assertion to mean anything");
                return span;
            }

            // The receive span stops only after the handler returns, on the pump's thread, so it can lag the
            // handler signal the test awaited. Bounded so a span that never arrives fails fast.
            public async Task<Activity> WaitForChatterSpanAsync(string operationType, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    var span = FindChatterSpan(operationType);
                    if (span != null)
                    {
                        return span;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                }

                return FindChatterSpan(operationType);
            }

            public void Dispose() => _listener.Dispose();

            private static bool IsAzureSdkSpan(Activity span)
                => span.Source.Name.StartsWith(AzureSdkActivitySourcePrefix, StringComparison.Ordinal);

            private bool ShouldSubscribeTo(ActivitySource source)
            {
                if (source.Name == HostActivitySourceName)
                {
                    return true;
                }

                if (source.Name == BrokerDiagnostics.ActivitySourceName)
                {
                    return _subscribeToChatter;
                }

                return _subscribeToAzureSdk && source.Name.StartsWith(AzureSdkActivitySourcePrefix, StringComparison.Ordinal);
            }

            private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options)
                => ActivitySamplingResult.AllDataAndRecorded;

            private static ActivitySamplingResult SampleAllDataUsingParentId(ref ActivityCreationOptions<string> options)
                => ActivitySamplingResult.AllDataAndRecorded;
        }
    }
}
