using System;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    public class ReceivedMessage
    {
        public Guid ConvGroupHandle { get; }
        public Guid ConvHandle { get; }
        public long MessageSeqNo { get; }
        public string ServiceName { get; }
        public string ServiceContractName { get; }
        public string MessageTypeName { get; }
        public byte[] Message { get; }

        public ReceivedMessage(Guid convGroupHandle,
                               Guid convHandle,
                               long messageSeqNo,
                               string serviceName,
                               string serviceContractName,
                               string messageTypeName,
                               byte[] message)
        {
            ConvGroupHandle = convGroupHandle;
            ConvHandle = convHandle;
            MessageSeqNo = messageSeqNo;
            ServiceName = serviceName;
            ServiceContractName = serviceContractName;
            MessageTypeName = messageTypeName;
            Message = message;
        }
    }
}
