namespace Chatter.Testing.Core
{
    /// <summary>
    /// This is a base test class providing the ability via a context to create test objects within other test objects
    /// </summary>
    public abstract class Context
    {
        protected INewContext New { get; }
    }
}
