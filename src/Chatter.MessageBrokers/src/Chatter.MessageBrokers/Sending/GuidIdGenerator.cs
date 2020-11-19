using System;

namespace Chatter.MessageBrokers.Sending
{
    public class GuidIdGenerator : IMessageIdGenerator
    {
        public Guid GenerateId(byte[] seedData = null) => Guid.NewGuid();
    }
}
