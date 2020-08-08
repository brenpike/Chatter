using System.Collections.Generic;
using System.Linq;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlip
    {
        private readonly IList<RoutingStep> _log;
        private readonly IList<AtomicRoutingStep> _visited;

        internal RoutingSlip()
        {
            _visited = new List<AtomicRoutingStep>();
            Route = new List<AtomicRoutingStep>();
            _log = new List<RoutingStep>();
        }

        public string Id { get; set; }
        public IList<AtomicRoutingStep> Route { get; internal set; }
        public IReadOnlyList<AtomicRoutingStep> Visited => (IReadOnlyList<AtomicRoutingStep>)_visited;
        public IReadOnlyList<RoutingStep> Log => (IReadOnlyList<RoutingStep>)_log;

        public void RouteToNextStep()
        {
            var currentStep = Route.First();

            _visited.Add(currentStep);

            _log.Add(currentStep.DestinationStep);

            Route.RemoveAt(0);
        }

        public void Compensate()
        {
            Route.Clear();

            foreach (var step in Visited.Reverse())
            {
                Route.Add(step);
            }

            var currentStep = Route.First();

            _visited.Add(currentStep);

            _log.Add(currentStep.CompensationStep);

            Route.RemoveAt(0);
        }
    }
}
