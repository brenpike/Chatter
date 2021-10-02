using Chatter.CQRS.Commands;

namespace Chatter.Testing.Core.Creators.CQRS
{
    public class CommandCreator : Creator<ICommand>
    {
        public CommandCreator(INewContext newContext, ICommand creation = null) 
            : base(newContext, creation)
        {
            Creation = new CommandWithNoProperties();
        }

        public CommandCreator ThatHasProperties()
        {
            Creation = new CommandWithProperties()
            {
                Property1 = 1,
                Property2 = "string"
            };
            return this;
        }

        private class CommandWithNoProperties : ICommand { }
        private class CommandWithProperties : ICommand
        {
            public int Property1 { get; set; }
            public string Property2 { get; set; }
        }
    }
}
