using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlipBuilder
    {
        private readonly string _id;
        private IList<AtomicRoutingStep> _route;

        private RoutingSlipBuilder(Guid id)
        {
            _id = id.ToString();
            _route = new List<AtomicRoutingStep>();
        }

        public static RoutingSlipBuilder NewRoutingSlip(Guid id)
        {
            return new RoutingSlipBuilder(id);
        }

        public RoutingSlipBuilder WithRoute(RoutingStepBuilder routingStepBuilder)
            => WithRoute(routingStepBuilder.Build());

        public RoutingSlipBuilder WithRoute(AtomicRoutingStep atomicRoutingStep)
        {
            _route.Add(atomicRoutingStep);
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
