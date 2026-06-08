using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlip
    {
        private IList<RoutingStep> _visited;

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
        [JsonInclude]
        public IReadOnlyList<RoutingStep> Visited
        {
            get => (IReadOnlyList<RoutingStep>)_visited;
            private set => _visited = value is null ? new List<RoutingStep>() : new List<RoutingStep>(value);
        }

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
