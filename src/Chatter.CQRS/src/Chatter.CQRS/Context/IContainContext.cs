namespace Chatter.CQRS.Context
{
    public interface IContainContext
    {
        /// <summary>
        /// A context container that support extensibility by holding additional context
        /// </summary>
        ContextContainer Container { get; }
    }
}
