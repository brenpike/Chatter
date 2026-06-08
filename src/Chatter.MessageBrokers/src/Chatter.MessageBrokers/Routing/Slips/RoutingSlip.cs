using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlip
    {
        private readonly IList<RoutingStep> _visited;

        [JsonConstructor]
        internal RoutingSlip()
        {
            _visited = new List<RoutingStep>();
            Route = new List<RoutingStep>();
            Attachments = new Dictionary<string, object>();
        }

        public Guid Id { get; set; }
        [JsonInclude]
        public IList<RoutingStep> Route { get; internal set; }
        [JsonInclude]
        public IDictionary<string, object> Attachments { get; internal set; }
        public IReadOnlyList<RoutingStep> Visited => (IReadOnlyList<RoutingStep>)_visited;

        public string RouteToNextStep()
        {
            var currentStep = Route.FirstOrDefault();

            if (currentStep == null)
            {
                return null;
            }

            _visited.Add(currentStep);

            Route.RemoveAt(0);

            return currentStep.DestinationPath;
        }
    }
}
