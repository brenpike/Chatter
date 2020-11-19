using System;

namespace Chatter.MessageBrokers.Sending
{
    public interface IMessageIdGenerator
    {
        Guid GenerateId(byte[] seedData = null);
    }
}
