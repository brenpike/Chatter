using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver
{
    // Pins the receiver's startup POISON-DESTINATION EXISTENCE gate (issue #366). The neither-configured gate only
    // proves a poison destination was NAMED; this one proves it EXISTS. This module provisions no topology, so a
    // dead-letter / error queue named in configuration but never declared externally makes the deadletter's
    // mandatory:true republish fault — the core catches and logs that, leaving the original delivery UNSETTLED.
    // At the default Prefetch of 1 the unacked delivery holds the receiver's only broker credit, so the receiver
    // stalls indefinitely on the first poison message and only a log line records it. The gate passively declares
    // each configured poison destination during InitializeAsync, BEFORE StartReceivingAsync registers the AMQP
    // consumer, so the misconfiguration surfaces loudly at startup instead of as a silent stall.
    public class WhenValidatingPoisonDestinations : Testing.Core.Context
    {
        [Fact]
        public void MustFailFastWhenDeadLetterQueueWasNeverDeclared()
        {
            InMemoryRabbitMqConnectionSource source = null;

            var ex = ReceiverHarness.CaptureInitException(
                deadLetterQueuePath: ReceiverHarness.DeadLetterPath,
                errorQueuePath: null,
                configure: captured =>
                {
                    source = captured;
                    captured.OnPublishChannelCreated = channel => channel.PassiveDeclareFault = NotFoundFor(ReceiverHarness.DeadLetterPath);
                });

            ex.Should().BeOfType<InvalidOperationException>("a dead-letter queue that was never declared externally must be rejected at startup, not discovered when the first poison message stalls the receiver");
            ex.Message.Should().Contain(ReceiverHarness.ReceiverPath, "the error must name the receiver whose startup was rejected");
            ex.Message.Should().Contain(ReceiverHarness.DeadLetterPath, "the error must name the missing queue");
            ex.Message.Should().Contain("DeadLetterQueuePath", "the error must name the option that configured the missing queue");

            // The gate runs BEFORE StartReceivingAsync: no consumer was ever registered, so the receiver never
            // began consuming against an unusable poison destination.
            source.ReceiveChannel.RegisteredConsumer.Should().BeNull("the existence gate must reject before the AMQP consumer is registered");
            source.ReceiveChannel.LastConsumeAutoAck.Should().BeNull("no BasicConsume may have been issued once the gate rejected startup");
        }

        [Fact]
        public void MustFailFastWhenErrorQueueWasNeverDeclared()
        {
            InMemoryRabbitMqConnectionSource source = null;

            var ex = ReceiverHarness.CaptureInitException(
                deadLetterQueuePath: null,
                errorQueuePath: ReceiverHarness.ErrorPath,
                configure: captured =>
                {
                    source = captured;
                    captured.OnPublishChannelCreated = channel => channel.PassiveDeclareFault = NotFoundFor(ReceiverHarness.ErrorPath);
                });

            ex.Should().BeOfType<InvalidOperationException>("an error-only configuration deadletters to the error queue, so its existence is just as load-bearing as a dead-letter queue's");
            ex.Message.Should().Contain(ReceiverHarness.ReceiverPath);
            ex.Message.Should().Contain(ReceiverHarness.ErrorPath, "the error must name the missing queue");
            ex.Message.Should().Contain("ErrorQueuePath", "the error must name the option that configured the missing queue");

            source.ReceiveChannel.RegisteredConsumer.Should().BeNull("the existence gate must reject before the AMQP consumer is registered");
        }

        [Fact]
        public void MustProbeEveryConfiguredPoisonDestinationAndStartWhenAllExist()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: ReceiverHarness.DeadLetterPath,
                                                 errorQueuePath: ReceiverHarness.ErrorPath);

            harness.StartupPassiveDeclares.Should().BeEquivalentTo(
                new[] { ReceiverHarness.DeadLetterPath, ReceiverHarness.ErrorPath },
                "BOTH configured poison destinations must be proven to exist before consumption starts");
            harness.ConnectionSource.ReceiveChannel.RegisteredConsumer.Should().NotBeNull(
                "declared poison destinations must let the receiver start consuming");
        }

        [Fact]
        public void MustProbeOnlyTheConfiguredPoisonDestination()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: null, errorQueuePath: ReceiverHarness.ErrorPath);

            harness.StartupPassiveDeclares.Should().Equal(
                new[] { ReceiverHarness.ErrorPath },
                "an error-only configuration names exactly one poison destination, so exactly one queue may be probed");
        }

        [Fact]
        public void MustProbeNothingUnderTransactionModeNone()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: ReceiverHarness.DeadLetterPath,
                                                 transactionMode: TransactionMode.None);

            harness.StartupPassiveDeclares.Should().BeEmpty(
                "TransactionMode.None is at-most-once — a poison message is dropped, never republished — so no poison destination is required and none may be probed");
            harness.ConnectionSource.AcquirePublishChannelCount.Should().Be(0,
                "a mode that requires no poison destination must not rent a publish channel to probe one");
            harness.ConnectionSource.ReceiveChannel.RegisteredConsumer.Should().NotBeNull(
                "TransactionMode.None must still start consuming");
        }

        [Fact]
        public void MustPropagateNonNotFoundFailuresUnwrapped()
        {
            // A connection-refused / auth / resource-locked fault is NOT a missing queue. Masking it as one would
            // send an operator to declare a queue that already exists, so it propagates exactly as thrown.
            var accessRefused = new OperationInterruptedException(
                new ShutdownEventArgs(ShutdownInitiator.Peer, Constants.AccessRefused, "ACCESS_REFUSED"));

            var ex = ReceiverHarness.CaptureInitException(
                deadLetterQueuePath: ReceiverHarness.DeadLetterPath,
                errorQueuePath: null,
                configure: captured => captured.OnPublishChannelCreated = channel => channel.PassiveDeclareFault = accessRefused);

            ex.Should().BeSameAs(accessRefused, "only a 404 NOT_FOUND means the queue does not exist; every other fault must surface unwrapped");
        }

        private static OperationInterruptedException NotFoundFor(string queue)
            => new OperationInterruptedException(
                new ShutdownEventArgs(ShutdownInitiator.Peer, Constants.NotFound, $"NOT_FOUND - no queue '{queue}'"));
    }
}
