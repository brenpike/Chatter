using System;
using System.Text;
using Chatter.MessageBrokers.Reliability.Outbox;
namespace Chatter.Testing.Core.Creators.MessageBrokers
{
    public class OutboxMessageCreator : Creator<OutboxMessage>
    {
        public OutboxMessageCreator(INewContext newContext, OutboxMessage creation = default) : base(newContext, creation)
        {
            Creation = new OutboxMessage
            {
                BatchId = Guid.NewGuid(),
                MessageId = Guid.NewGuid().ToString(),
                ProcessedFromOutboxAtUtc = DateTime.Now,
                Destination = "destination",
                SentToOutboxAtUtc = DateTime.Now,
                Body = Encoding.ASCII.GetBytes("body"),
                StringifiedMessage = "body"
            };
        }

        public OutboxMessageCreator ThatIsNotProcessed()
        {
            Creation.ProcessedFromOutboxAtUtc = null;
            return this;
        }
    }
}
