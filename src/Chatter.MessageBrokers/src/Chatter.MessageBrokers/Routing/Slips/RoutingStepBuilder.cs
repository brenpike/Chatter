namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingStepBuilder
    {
        private string _destinationPath;
        private string _compensationPath;

        private RoutingStepBuilder(string destinationPath)
            => _destinationPath = destinationPath;

        public static RoutingStepBuilder WithStep(string destinationPath) 
            => new RoutingStepBuilder(destinationPath);

        public RoutingStepBuilder WithCompensatingStep(string compensatingPath)
        {
            _compensationPath = compensatingPath;
            return this;
        }

        public CompensatingRoutingStep Build() 
            => new CompensatingRoutingStep(_destinationPath, _compensationPath);
    }
}
