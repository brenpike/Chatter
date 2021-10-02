namespace Chatter.Testing.Core.Creators.Common
{
    public static class NewExtensions
    {
        public static NewCommon Common(this INewContext context) => new NewCommon(context);
        public class NewCommon
        {
            private INewContext NewContext { get; }
            public NewCommon(INewContext context) => NewContext = context;
            public LoggerCreator<T> Logger<T>() => new LoggerCreator<T>(NewContext);
            public AssemblyCreator Assembly => new AssemblyCreator(NewContext);
            public TypeCreator Type => new TypeCreator(NewContext);
        }
    }
}
