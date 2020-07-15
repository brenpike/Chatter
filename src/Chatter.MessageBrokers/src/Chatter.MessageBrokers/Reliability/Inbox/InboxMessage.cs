using System;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public class InboxMessage
    {
        public string MessageId { get; set; }
        public DateTime? ReceivedByInboxAtUtc { get; set; }
    }
}
