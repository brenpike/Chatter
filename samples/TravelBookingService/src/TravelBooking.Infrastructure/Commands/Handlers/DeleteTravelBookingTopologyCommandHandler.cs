using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Microsoft.Azure.ServiceBus.Management;
using System;
using System.Linq;
using System.Threading.Tasks;
using TravelBooking.Application.Commands;

namespace TravelBooking.Infrastructure.Commands.Handlers
{
    public class DeleteTravelBookingTopologyCommandHandler : IMessageHandler<DeleteTravelBookingTopologyCommand>
    {
        private readonly ServiceBusOptions _serviceBusConfiguration;

        public DeleteTravelBookingTopologyCommandHandler(ServiceBusOptions serviceBusConfiguration)
        {
            _serviceBusConfiguration = serviceBusConfiguration;
        }

        public async Task Handle(DeleteTravelBookingTopologyCommand command, IMessageHandlerContext context)
        {
            var nm = new ManagementClient(_serviceBusConfiguration.ConnectionString);

            try
            {
                foreach (var queueDescription in TopologyDefinition.QueueDescriptions().Reverse())
                {
                    await nm.DeleteQueueAsync(queueDescription.Path);
                }
            }
            finally
            {
                await nm.CloseAsync();
            }
        }
    }
}
