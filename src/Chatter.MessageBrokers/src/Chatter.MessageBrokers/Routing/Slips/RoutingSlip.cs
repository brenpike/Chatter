using System;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlip
    {
        private readonly IList<RoutingStep> _log;
        private readonly IList<CompensatingRoutingStep> _visited;

        internal RoutingSlip()
        {
            _visited = new List<CompensatingRoutingStep>();
            Route = new List<CompensatingRoutingStep>();
            _log = new List<RoutingStep>();
            Attachments = new Dictionary<string, object>();
        }

        public Guid Id { get; set; }
        public IList<CompensatingRoutingStep> Route { get; internal set; }
        public IDictionary<string, object> Attachments { get; internal set; }
        public IReadOnlyList<CompensatingRoutingStep> Visited => (IReadOnlyList<CompensatingRoutingStep>)_visited;
        public IReadOnlyList<RoutingStep> Log => (IReadOnlyList<RoutingStep>)_log;

        public string RouteToNextStep()
        {
            var currentStep = Route.First();

            _visited.Add(currentStep);

            _log.Add(currentStep);

            Route.RemoveAt(0);

            return currentStep.DestinationPath;
        }

        public void Compensate()
        {
            if (Visited.All(r => string.IsNullOrWhiteSpace(r.CompensationPath)))
            {
                return;
            }

            Route.Clear();

            foreach (var step in Visited.Reverse())
            {
                Route.Add(new CompensatingRoutingStep(step.CompensationPath, null));
            }
        }
    }
}
