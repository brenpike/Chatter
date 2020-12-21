namespace Chatter.MessageBrokers.SqlServiceBroker
{
    public static class SSBMessageContext
    {
        public static readonly string SqlServiceBrokerBaseHeader = $"{MessageContext.ChatterBaseHeader}.SqlServiceBroker";
        public static readonly string InfrastructureType = $"{MessageContext.ChatterBaseHeader}.SqlServiceBroker";

        public static readonly string ConversationGroupId = $"{SqlServiceBrokerBaseHeader}.ConversationGroupId";
        public static readonly string ConversationHandle = $"{SqlServiceBrokerBaseHeader}.ConversationHandle";
        public static readonly string MessageSequenceNumber = $"{SqlServiceBrokerBaseHeader}.MessageSequenceNumber";
        public static readonly string ServiceName = $"{SqlServiceBrokerBaseHeader}.ServiceName";
        public static readonly string ServiceContractName = $"{SqlServiceBrokerBaseHeader}.ServiceContractName";
        public static readonly string MessageTypeName = $"{SqlServiceBrokerBaseHeader}.MessageTypeName";
    }
}
