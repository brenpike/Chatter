using Chatter.CQRS.Commands;

namespace Chatter.Testing.Core.Creators.CQRS
{
    public static class NewExtensions
    {
        public static NewCqrs Cqrs(this INewContext context) => new NewCqrs(context);

        public class NewCqrs
        {
            private INewContext NewContext { get; }

            public NewCqrs(INewContext context) => NewContext = context;
            public CommandBehaviorCreator<TCommand> CommandBehavior<TCommand>() where TCommand : ICommand => new CommandBehaviorCreator<TCommand>(NewContext);
            public AssemblyFilterSourceProviderCreator AssemblyFilterSourceProvider => new AssemblyFilterSourceProviderCreator(NewContext);
        }
    }
}
