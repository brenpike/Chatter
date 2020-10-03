using System;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingStep
    {
        internal RoutingStep(string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException("A destination is required for a step of a routing slip.", nameof(destinationPath));
            }

            DestinationPath = destinationPath;
        }

        public string DestinationPath { get; set; }
    }
}
