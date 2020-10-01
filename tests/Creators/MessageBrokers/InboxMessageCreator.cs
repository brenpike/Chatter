using Chatter.MessageBrokers.Reliability.Inbox;
using System;

namespace Chatter.Testing.Core.Creators.MessageBrokers
{
    public class InboxMessageCreator : Creator<InboxMessage>
    {
        public InboxMessageCreator(INewContext newContext, InboxMessage creation = default)
            : base(newContext, creation)
        {
            Creation = new InboxMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                ReceivedByInboxAtUtc = DateTime.UtcNow
            };
        }

        public InboxMessageCreator ThatIsNotRecieved()
        {
            Creation.ReceivedByInboxAtUtc = null;
            return this;
        }
    }
}
