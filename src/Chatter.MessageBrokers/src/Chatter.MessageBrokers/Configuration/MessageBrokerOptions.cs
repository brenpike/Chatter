using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Saga.Configuration;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Configuration
{
    public class MessageBrokerOptions
    {
        public ReliabilityOptions Reliability { get; set; }
        public List<SagaOptions> Sagas { get; set; }
    }
}
