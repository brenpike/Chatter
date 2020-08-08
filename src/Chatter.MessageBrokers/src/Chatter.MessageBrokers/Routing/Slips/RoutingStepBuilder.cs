namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingStepBuilder
    {
        private RoutingStep _destinationStep;
        private RoutingStep _compensationStep;

        private RoutingStepBuilder(string destinationPath)
        {
            _destinationStep = new RoutingStep()
            {
                DestinationPath = destinationPath
            };
        }

        public static RoutingStepBuilder WithStep(string destinationPath) 
            => new RoutingStepBuilder(destinationPath);

        public RoutingStepBuilder WithCompensatingStep(string destinationPath)
        {
            _compensationStep = new RoutingStep()
            {
                DestinationPath = destinationPath
            };

            return this;
        }

        public AtomicRoutingStep Build()
        {
            return new AtomicRoutingStep()
            {
                DestinationStep = _destinationStep,
                CompensationStep = _compensationStep
            };
        }
    }
}
