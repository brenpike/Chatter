using Newtonsoft.Json;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingStep
    {
        [JsonConstructor]
        private RoutingStep() { }

        internal RoutingStep(string destinationPath) 
            => DestinationPath = destinationPath;

        public string DestinationPath { get; set; }
    }
}
