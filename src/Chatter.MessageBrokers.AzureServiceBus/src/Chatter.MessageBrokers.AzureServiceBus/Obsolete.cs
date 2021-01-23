using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    public partial class ServiceBusOptionsBuilder
    {
        [Obsolete("This method will be deprecated in version 0.3.0. Use ServiceBusOptionsBuilder.UseConfig instead.", false)]
        public ServiceBusOptionsBuilder AddServiceBusOptions(string configSectionName = _defaultAzureServiceBusSectionName)
        {
            _azureServiceBusSectionName = configSectionName;
            _serviceBusOptionsSection = _configuration.GetSection(configSectionName);
            return this;
        }
    }
}
