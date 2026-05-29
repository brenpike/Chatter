using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;

namespace Chatter.Testing.Core.Creators.MessageBrokers
{
    public static class NewExtensions
    {
        public static NewMessageBrokers MessageBrokers(this INewContext context)
        {
            return new NewMessageBrokers(context);
        }

        public class NewMessageBrokers
        {
            private INewContext NewContext { get; }

            public NewMessageBrokers(INewContext context)
            {
                NewContext = context;
            }

            public DbContextCreator DbContext()
            {
                return new DbContextCreator(NewContext);
            }

            public OutboxMessageCreator OutboxMessage()
            {
                return new OutboxMessageCreator(NewContext);
            }

            public InboxMessageCreator InboxMessage()
            {
                return new InboxMessageCreator(NewContext);
            }

            public NewRecovery Recovery()
            {
                return new NewRecovery(NewContext);
            }
        }

        public class NewRecovery
        {
            private INewContext NewContext { get; }

            public NewRecovery(INewContext context)
            {
                NewContext = context;
            }

            public RecoveryOptionsCreator RecoveryOptions()
            {
                return new RecoveryOptionsCreator(NewContext);
            }

            public CircuitBreakerOptionsCreator CircuitBreakerOptions()
            {
                return new CircuitBreakerOptionsCreator(NewContext);
            }
        }
    }
}
