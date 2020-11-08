namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingStepBuilder
    {
        private string _destinationPath;

        private RoutingStepBuilder(string destinationPath)
            => _destinationPath = destinationPath;

        public static RoutingStepBuilder WithStep(string destinationPath)
            => new RoutingStepBuilder(destinationPath);

        public RoutingStep Build()
            => new RoutingStep(_destinationPath);
    }
}
