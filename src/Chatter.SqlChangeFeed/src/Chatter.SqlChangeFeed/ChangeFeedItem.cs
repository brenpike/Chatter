using Chatter.CQRS;
using Chatter.CQRS.Commands;
using System;

namespace Chatter.SqlChangeFeed
{
    public class ChangeFeedItem<TRowChangeData> : ICommand where TRowChangeData : class, IMessage
    {
        public Guid TraceId { get; set; }
        public TRowChangeData Inserted { get; set; }
        public TRowChangeData Deleted { get; set; }
    }
}
