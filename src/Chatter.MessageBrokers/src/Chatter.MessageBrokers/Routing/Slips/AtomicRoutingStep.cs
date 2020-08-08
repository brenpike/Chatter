namespace Chatter.MessageBrokers.Routing.Slips
{
    public class AtomicRoutingStep
    {
        public RoutingStep DestinationStep { get; set; }
        public RoutingStep CompensationStep { get; set; }
    }
}
