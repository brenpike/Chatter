using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public class InboxBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
    {
        private readonly IBrokeredMessageInbox _brokeredMessageInbox;
        private readonly ILogger<InboxBehavior<TMessage>> _logger;

        public InboxBehavior(IBrokeredMessageInbox brokeredMessageInbox, ILogger<InboxBehavior<TMessage>> logger)
        {
            _brokeredMessageInbox = brokeredMessageInbox ?? throw new ArgumentNullException(nameof(brokeredMessageInbox));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            _logger.LogDebug($"Entering {nameof(InboxBehavior<TMessage>)}");

            // INVARIANT: only the Delivery Entry - the message the current receive attempt admitted first - is received
            // via the inbox; every other command dispatched on the same broker context in that attempt (nested
            // in-process dispatch inherits it) goes straight to next(), because the entry's handler already holds the
            // delivery's message id (ADR-0041). The entry is attempt-scoped: the receiver installs a fresh one at the
            // start of every Recovery attempt (BrokeredMessageReceiver.BeginReceiveAttempt), so a retry that constructs
            // a fresh command is gated again; GetOrNew still creates one lazily for a dispatch that never passed that
            // seam. When the delivered payload is an Event the Inbox never sees it, so the attempt's first Command
            // takes the entry and later sibling Commands in that attempt are at-least-once on retry or redelivery
            // (ADR-0041 G2; see #539). No test pins that bound.
            // Pinned by WhenGatingTheInboxAcrossRecoveryAttempts: deleting the Include in BeginReceiveAttempt
            // reddens MustGateTheFirstCommandOfEveryRecoveryAttemptWhenTheHandlerBuildsAFreshOne and
            // MustGateTheFirstCommandOfEveryAttemptWhenAReceiverOverridesTheDispatch. Pinned by WhenHandling: restoring the bare `is IMessageBrokerContext` gate
            // reddens MustInvokeTheNestedHandlerWhenAGatedHandlersOwnDispatchReEntersTheBehavior,
            // MustReceiveViaInboxOnlyTheMessageTheDeliveryAdmitted and
            // MustInvokeTheHandlerOfASiblingDispatchedAfterTheAdmittedMessageCompleted; binding on a first-call flag
            // instead of the instance reddens MustReceiveViaInboxAgainWhenTheAdmittedMessageIsRedispatchedAfterItsHandlerFailed.
            // A handler that re-dispatches the received INSTANCE itself is gated again - the Admits() check
            // reports true for the same instance on every call, so it reaches ReceiveViaInbox again - and what
            // happens next is the store's own duplicate rule: the in-memory store skips it, while the relational
            // and standalone Cosmos stores re-claim or take over the still-open claim and run it (EF
            // BrokeredMessageInbox, Chatter.MessageBrokers.Reliability.Cosmos CosmosBrokeredMessageInbox); no test
            // pins that.
            if (messageHandlerContext is IMessageBrokerContext messageBrokerContext
                && messageBrokerContext.Container.GetOrNew<InboxDeliveryEntry>().Admits(message))
            {
                return _brokeredMessageInbox.ReceiveViaInbox(message, messageBrokerContext, () => next());
            }

            return next();
        }
    }
}
