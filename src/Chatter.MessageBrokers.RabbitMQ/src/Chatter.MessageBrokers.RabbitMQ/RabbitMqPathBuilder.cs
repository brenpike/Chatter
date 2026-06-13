namespace Chatter.MessageBrokers.RabbitMQ
{
    /// <summary>
    /// The RabbitMQ <see cref="IBrokeredMessagePathBuilder"/>. RabbitMQ addresses receivers by queue name
    /// under the default-exchange convention (routing key = Destination = queue name), so each path is the
    /// receiver/queue name verbatim — there is no topic/subscription or rule entity to compose into the path
    /// (unlike Azure Service Bus). Error and dead-letter queue names are carried on the receiver's
    /// <c>ReceiverOptions</c> (the attribute-declared ErrorQueueName / DeadletterQueueName) and resolved by the
    /// receiver's dead-letter republish, not through this seam, which exposes only sending/receiving/rule paths.
    /// </summary>
    class RabbitMqPathBuilder : IBrokeredMessagePathBuilder
    {
        string IBrokeredMessagePathBuilder.GetMessageSendingPath(string messageSendingPath)
            => messageSendingPath;

        string IBrokeredMessagePathBuilder.GetMessageReceivingPath(string messageSendingPath, string messageReceiverPath)
            => messageReceiverPath;

        string IBrokeredMessagePathBuilder.GetMessageReceivingRulePath(string messageSendingPath, string messageReceiverPath, string ruleName)
            => messageReceiverPath;
    }
}
