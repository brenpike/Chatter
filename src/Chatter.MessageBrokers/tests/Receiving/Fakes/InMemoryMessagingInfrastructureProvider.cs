using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Moq;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Tests.Receiving.Fakes
{
    /// <summary>
    /// In-memory test double for <see cref="IMessagingInfrastructureProvider"/>.
    /// Returns the supplied <see cref="InMemoryMessagingInfrastructureReceiver"/> from
    /// <see cref="GetReceiver"/> and provides a real <see cref="DefaultBrokeredMessagePathBuilder"/>
    /// via <see cref="GetInfrastructure"/> so
    /// <c>BrokeredMessageReceiver.StartReceiverImpl</c> can resolve the message receiving path.
    /// </summary>
    public sealed class InMemoryMessagingInfrastructureProvider : IMessagingInfrastructureProvider
    {
        private readonly InMemoryMessagingInfrastructureReceiver _receiver;
        private readonly IMessagingInfrastructure _infrastructure;

        // INVARIANT: optional opt-in gate that holds the SYNCHRONOUS GetReceiver until ReleaseGetReceiverGate is called,
        // plus an entry-signal the test awaits. Pins the SUT AFTER the NotStarted->Starting CAS but BEFORE
        // _infrastructureReceiver is assigned (the null-receiver claim window the InitializeAsync gate cannot reach, since
        // InitializeAsync runs only after _infrastructureReceiver is already assigned). Default null so the many existing
        // tests that construct this fake are unaffected; only the null-receiver-window test arms it. Mirrors the receiver
        // fake's ArmInitializeGate/ReleaseInitializeGate/entry-signal TCS trio idiom.
        private TaskCompletionSource<bool> _getReceiverGate;
        private TaskCompletionSource<bool> _getReceiverGateEntered;

        public InMemoryMessagingInfrastructureProvider(InMemoryMessagingInfrastructureReceiver receiver)
        {
            _receiver = receiver ?? throw new System.ArgumentNullException(nameof(receiver));

            // INVARIANT: MessagingInfrastructure's two-arg constructor injects DefaultBrokeredMessagePathBuilder
            // internally; the test project has InternalsVisibleTo access so DefaultBrokeredMessagePathBuilder
            // is resolvable, but we use the public MessagingInfrastructure ctor here to stay on the public API.
            var dispatcherFactory = new Mock<IMessagingInfrastructureDispatcherFactory>();
            dispatcherFactory.Setup(f => f.Create()).Returns(new Mock<IMessagingInfrastructureDispatcher>().Object);

            var receiverFactory = new Mock<IMessagingInfrastructureReceiverFactory>();
            receiverFactory.Setup(f => f.Create()).Returns(_receiver);

            _infrastructure = new MessagingInfrastructure(
                type: InfrastructureType,
                receiveInfrastructure: receiverFactory.Object,
                dispatchInfrastructure: dispatcherFactory.Object);
        }

        /// <summary>The infrastructure type key used for all lookups.</summary>
        public const string InfrastructureType = "in-memory-test";

        // ------------------------------------------------------------------ public test API

        /// <summary>
        /// Arms an opt-in gate that holds the synchronous <see cref="GetReceiver"/> until <see cref="ReleaseGetReceiverGate"/>
        /// is called, pinning the SUT after the NotStarted-&gt;Starting CAS but before <c>_infrastructureReceiver</c> is
        /// assigned. Returns the entry-signal the test awaits so the interleave is wall-clock-free: it completes when
        /// <see cref="GetReceiver"/> has reached the gate. Mirrors the receiver fake's <c>ArmInitializeGate</c> idiom.
        /// </summary>
        public Task ArmGetReceiverGate()
        {
            _getReceiverGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _getReceiverGateEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _getReceiverGateEntered.Task;
        }

        /// <summary>Releases the gate armed by <see cref="ArmGetReceiverGate"/> so the blocked <see cref="GetReceiver"/> returns.</summary>
        public void ReleaseGetReceiverGate() => _getReceiverGate?.TrySetResult(true);

        // ------------------------------------------------------------------ IMessagingInfrastructureProvider

        /// <summary>Returns an <see cref="IMessagingInfrastructure"/> whose PathBuilder is the real
        /// <see cref="DefaultBrokeredMessagePathBuilder"/> so path resolution succeeds.</summary>
        public IMessagingInfrastructure GetInfrastructure(string type)
            => _infrastructure;

        /// <summary>Returns the <see cref="InMemoryMessagingInfrastructureReceiver"/> supplied at construction.</summary>
        public IMessagingInfrastructureReceiver GetReceiver(string type)
        {
            // When a test arms the gate, signal entry and then block here — the SUT has advanced past the
            // NotStarted->Starting CAS but has NOT yet assigned _infrastructureReceiver (this call's return value), so the
            // test can land a teardown in the null-receiver window. Signal BEFORE blocking so the awaiting test observes
            // entry; block via GetAwaiter().GetResult() (no captured sync context) to match the fake's TCS idiom.
            var gate = _getReceiverGate;
            if (gate != null)
            {
                _getReceiverGateEntered?.TrySetResult(true);
                gate.Task.GetAwaiter().GetResult();
            }

            return _receiver;
        }

        /// <summary>Not exercised by the receiver loop; throws to make unexpected calls visible.</summary>
        public IMessagingInfrastructureDispatcher GetDispatcher(string type)
            => throw new System.NotImplementedException(
                $"{nameof(InMemoryMessagingInfrastructureProvider)}.{nameof(GetDispatcher)} is not used by the receiver loop.");
    }
}
