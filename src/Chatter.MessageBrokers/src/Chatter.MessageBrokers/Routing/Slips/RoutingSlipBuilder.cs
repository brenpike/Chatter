using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlipBuilder
    {
        private readonly Guid _id;
        private IList<RoutingStep> _route;

        private RoutingSlipBuilder(Guid id)
        {
            _id = id;
            _route = new List<RoutingStep>();
        }

        public static RoutingSlipBuilder NewRoutingSlip(Guid id) 
            => new RoutingSlipBuilder(id);

        public RoutingSlipBuilder WithRoute(RoutingStepBuilder routingStepBuilder)
            => WithRoute(routingStepBuilder.Build());

        public RoutingSlipBuilder WithRoute(RoutingStep step)
        {
            _route.Add(step);
            return this;
        }

        public RoutingSlipBuilder WithRoute(string step)
        {
            _route.Add(new RoutingStep(step));
            return this;
        }

        public RoutingSlip Build()
        {
            return new RoutingSlip()
            {
                Id = _id,
                Route = _route
            };
        }
    }
}
