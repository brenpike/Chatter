using Chatter.CQRS;
using Chatter.CQRS.Commands;
using System.Collections.Generic;

namespace Chatter.TableWatcher
{
    public class ProcessTableChangesCommand<TRowChangeData> : ICommand where TRowChangeData : class, IMessage
    {
        public IEnumerable<TRowChangeData> Inserted { get; set; } = new List<TRowChangeData>();
        public IEnumerable<TRowChangeData> Deleted { get; set; } = new List<TRowChangeData>();
    }
}
