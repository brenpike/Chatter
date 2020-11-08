using Microsoft.Azure.ServiceBus.Primitives;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus
{
    public class NullTokenProvider : ITokenProvider
    {
        Task<SecurityToken> ITokenProvider.GetTokenAsync(string appliesTo, TimeSpan timeout) 
            => Task.FromResult(new SecurityToken("token", DateTime.Now, "audience", string.Empty));
    }
}
