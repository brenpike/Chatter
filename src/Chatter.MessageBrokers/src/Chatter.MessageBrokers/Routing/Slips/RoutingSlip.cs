using System;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlip
    {
        private readonly IList<RoutingStep> _visited;

        internal RoutingSlip()
        {
            _visited = new List<RoutingStep>();
            Route = new List<RoutingStep>();
            Attachments = new Dictionary<string, object>();
        }

        public Guid Id { get; set; }
        public IList<RoutingStep> Route { get; internal set; }
        public IDictionary<string, object> Attachments { get; internal set; }
        public IReadOnlyList<RoutingStep> Visited => (IReadOnlyList<RoutingStep>)_visited;

        public string RouteToNextStep()
        {
            var currentStep = Route.First();

            _visited.Add(currentStep);

            Route.RemoveAt(0);

            return currentStep.DestinationPath;
        }
    }
}
