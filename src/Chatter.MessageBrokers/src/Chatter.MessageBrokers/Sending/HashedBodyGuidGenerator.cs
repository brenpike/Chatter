using System;
using System.Security.Cryptography;

namespace Chatter.MessageBrokers.Sending
{
    public class HashedBodyGuidGenerator : IMessageIdGenerator
    {
        public Guid GenerateId(byte[] seedData = null)
        {
            if (seedData is null)
            {
                return Guid.NewGuid();
            }

            using var sha265Provider = new SHA256CryptoServiceProvider();
            var hash = sha265Provider.ComputeHash(seedData);

            byte[] guidArray = Guid.NewGuid().ToByteArray();
            Array.Copy(hash, guidArray, 16);

            return new Guid(guidArray);
        }
    }
}
