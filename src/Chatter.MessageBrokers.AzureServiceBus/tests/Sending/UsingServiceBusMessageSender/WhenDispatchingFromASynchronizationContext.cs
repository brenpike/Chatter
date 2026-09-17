using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingServiceBusMessageSender
{
    // Pins the CONTINUATION SCHEDULING of the batch Dispatch overload. Dispatch awaits its sends inside
    // the transaction scope, so a continuation that is posted back to the caller's synchronization context
    // cannot run while that caller blocks on the returned task - the scope is never completed or disposed
    // and the dispatch hangs. The awaits must therefore not capture the caller's context.
    public class WhenDispatchingFromASynchronizationContext : Testing.Core.Context
    {
        private readonly byte[] _body = new byte[] { 1, 2, 3 };
        private readonly JsonBodyConverter _converter = new JsonBodyConverter();

        private OutboundBrokeredMessage CreateBrokeredMessage(string messageId)
            => new OutboundBrokeredMessage(messageId, _body, new Dictionary<string, object>(), "destination", _converter);

        // Models a single-threaded caller (UI / legacy sync host): every posted continuation is queued for a
        // pump that only the calling thread runs. Nothing queued here ever executes while that thread blocks.
        private sealed class BlockingSynchronizationContext : SynchronizationContext
        {
            private readonly List<SendOrPostCallback> _posted = new List<SendOrPostCallback>();

            public int PostedCount
            {
                get { lock (_posted) { return _posted.Count; } }
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                lock (_posted) { _posted.Add(d); }
            }

            public override void Send(SendOrPostCallback d, object state) => d(state);
        }

        // Hands back senders whose sends stay in flight until the gates are opened, without awaiting
        // anything itself, so the only context-capturing await in play is the one inside Dispatch.
        private sealed class GatedSenderFactory : IServiceBusMessageSenderFactory
        {
            private readonly List<TaskCompletionSource<bool>> _gates = new List<TaskCompletionSource<bool>>();

            public ServiceBusSender Create(string destinationEntityPath)
            {
                var sender = new Mock<ServiceBusSender>();
                sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
                      .Returns((ServiceBusMessage _, CancellationToken __) => StartGatedSend());
                return sender.Object;
            }

            public void OpenEveryGate()
            {
                lock (_gates)
                {
                    foreach (var gate in _gates)
                    {
                        gate.TrySetResult(true);
                    }
                }
            }

            private Task StartGatedSend()
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gates) { _gates.Add(gate); }
                return gate.Task;
            }
        }

        [Fact]
        public void MustNotCaptureTheCallersSynchronizationContextWhenAwaitingSends()
        {
            var original = SynchronizationContext.Current;
            var context = new BlockingSynchronizationContext();

            try
            {
                SynchronizationContext.SetSynchronizationContext(context);

                var senderFactory = new GatedSenderFactory();
                var sut = new ServiceBusMessageSender(senderFactory, new DispatchTransactionScopeFactory());

                var dispatchTask = sut.Dispatch(new[] { CreateBrokeredMessage("first"), CreateBrokeredMessage("second") },
                                                new TransactionContext("receiver", TransactionMode.None));

                // The broker SDK completes sends off the caller's thread; mirror that here.
                ThreadPool.QueueUserWorkItem(_ => senderFactory.OpenEveryGate());

                // The caller blocks the context's only thread, so nothing posted back to it can ever run.
                // Blocking is the behaviour under test, so the xUnit blocking-call analyzer is suppressed here.
#pragma warning disable xUnit1031
                dispatchTask.Wait(TimeSpan.FromSeconds(10))
                            .Should().BeTrue("a dispatch that captured the caller's synchronization context deadlocks a synchronously waiting caller");
#pragma warning restore xUnit1031

                context.PostedCount.Should().Be(0, "no dispatch continuation may be queued to the caller's synchronization context");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(original);
            }
        }
    }
}
