using Chatter.CQRS.Events;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.SqlTableWatcher
{
    public class SqlMessageEnvelope<TMessageData> where TMessageData : IEvent
    {
        public IEnumerable<TMessageData> Inserted { get; set; }
        public IEnumerable<TMessageData> Deleted { get; set; }

        public ChangeType GetChangeType()
        {
            if (Deleted is null || Deleted?.Count() == 0)
            {
                return ChangeType.Insert;
            }

            if (Inserted is null || Inserted?.Count() == 0)
            {
                return ChangeType.Delete;
            }

            return ChangeType.Update;
        }
    }
}
