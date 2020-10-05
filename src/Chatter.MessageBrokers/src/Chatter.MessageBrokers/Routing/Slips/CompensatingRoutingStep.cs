using Newtonsoft.Json;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class CompensatingRoutingStep : RoutingStep
    {
        [JsonConstructor]
        private CompensatingRoutingStep()
            : base(null) {}

        internal CompensatingRoutingStep(string slip, string compensatingSlip)
            : base(slip)
        {
            CompensationPath = compensatingSlip;
        }

        public string CompensationPath { get; set; }
    }
}
