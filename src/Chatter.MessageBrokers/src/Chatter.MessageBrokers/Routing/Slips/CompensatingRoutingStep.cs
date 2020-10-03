namespace Chatter.MessageBrokers.Routing.Slips
{
    public class CompensatingRoutingStep : RoutingStep
    {
        internal CompensatingRoutingStep(string slip, string compensatingSlip)
            : base(slip)
        {
            CompensationPath = compensatingSlip;
        }

        public string CompensationPath { get; set; }
    }
}
