using Chatter.CQRS;
using Chatter.CQRS.Commands;
using System.Collections.Generic;

namespace Chatter.SqlChangeFeed
{
    public class ProcessChangeFeedCommand<TRowChangeData> : ICommand where TRowChangeData : class, IMessage
    {
        public IEnumerable<ChangeFeedItem<TRowChangeData>> Changes { get; set; } = new List<ChangeFeedItem<TRowChangeData>>();
    }
}
