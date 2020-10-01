namespace Chatter.Testing.Core.Creators.CQRS
{
    public static class NewExtensions
    {
        public static NewCqrs Cqrs(this INewContext context)
        {
            return new NewCqrs(context);
        }

        public class NewCqrs
        {
            private INewContext NewContext { get; }

            public NewCqrs(INewContext context)
            {
                NewContext = context;
            }

            public CommandBehaviorCreator CommandBehavior()
            {
                return new CommandBehaviorCreator(NewContext);
            }
        }
    }
}
