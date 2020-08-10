using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Context;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Core
{
    /// <summary>
    /// An <see cref="ICompensationRoutingStrategy"/> implementation that performs compensation by deadlettering the current message.
    /// This assumes the queue has been configured with ForwardDeadLetteredMessagesTo to the compensating queue./>
    /// </summary>
    public class DeadLetterCompensationStrategy : IRouteCompensationMessages
    {
        ///<inheritdoc/>
        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, CompensationRoutingContext compensateContext)
        {
            if (!(compensateContext.Container.TryGet<Message>(out var receivedMessage)))
            {
                throw new InvalidOperationException($"The received {nameof(CompensationRoutingContext)} did not contain a {typeof(Message).Name}");
            }

            if (!(transactionContext.Container.TryGet<MessageReceiver>(out var receiver)))
            {
                throw new InvalidOperationException($"The received {nameof(TransactionContext)} did not contain a {typeof(MessageReceiver).Name}");
            }

            return receiver.DeadLetterAsync(receivedMessage.SystemProperties.LockToken, (IDictionary<string, object>)inboundBrokeredMessage.ApplicationProperties);
        }
    }
}
