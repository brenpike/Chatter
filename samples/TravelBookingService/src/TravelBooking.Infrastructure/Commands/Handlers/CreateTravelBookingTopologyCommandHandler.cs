using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Microsoft.Azure.ServiceBus.Management;
using System.Threading.Tasks;
using TravelBooking.Application.Commands;

namespace TravelBooking.Infrastructure.Commands.Handlers
{
    public class CreateTravelBookingTopologyCommandHandler : IMessageHandler<CreateTravelBookingTopologyCommand>
    {
        private readonly ServiceBusOptions _serviceBusConfiguration;

        public CreateTravelBookingTopologyCommandHandler(ServiceBusOptions serviceBusConfiguration)
        {
            _serviceBusConfiguration = serviceBusConfiguration;
        }

        public async Task Handle(CreateTravelBookingTopologyCommand command, IMessageHandlerContext context)
        {
            var nm = new ManagementClient(_serviceBusConfiguration.ConnectionString);
            try
            {
                foreach (var qd in TopologyDefinition.QueueDescriptions())
                {
                    if (await nm.QueueExistsAsync(qd.Path))
                    {
                        await nm.GetQueueAsync(qd.Path);
                    }
                    else
                    {
                        await nm.CreateQueueAsync(qd);
                    }
                }

                foreach (var qd in TopologyDefinition.TopicDescriptions())
                {
                    if (await nm.TopicExistsAsync(qd.Path))
                    {
                        await nm.GetTopicAsync(qd.Path);
                    }
                    else
                    {
                        await nm.CreateTopicAsync(qd);
                    }
                }

                foreach (var qd in TopologyDefinition.TopicSubscriptionDescriptions())
                {
                    if (await nm.SubscriptionExistsAsync(qd.TopicPath, qd.SubscriptionName))
                    {
                        await nm.GetSubscriptionAsync(qd.TopicPath, qd.SubscriptionName);
                    }
                    else
                    {
                        await nm.CreateSubscriptionAsync(qd.TopicPath, qd.SubscriptionName);
                    }
                }
            }
            finally
            {
                await nm.CloseAsync();
            }
        }
    }
}
