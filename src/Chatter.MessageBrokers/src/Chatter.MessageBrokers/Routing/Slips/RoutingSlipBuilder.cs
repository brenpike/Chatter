using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlipBuilder
    {
        private readonly Guid _id;
        private IList<CompensatingRoutingStep> _route;

        private RoutingSlipBuilder(Guid id)
        {
            _id = id;
            _route = new List<CompensatingRoutingStep>();
        }

        public static RoutingSlipBuilder NewRoutingSlip(Guid id) 
            => new RoutingSlipBuilder(id);

        public RoutingSlipBuilder WithRoute(RoutingStepBuilder routingStepBuilder)
            => WithRoute(routingStepBuilder.Build());

        public RoutingSlipBuilder WithRoute(CompensatingRoutingStep slip)
        {
            _route.Add(slip);
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
