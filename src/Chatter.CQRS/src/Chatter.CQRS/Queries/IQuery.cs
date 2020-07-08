namespace Chatter.CQRS.Queries
{
    /// <summary>
    /// Marker
    /// </summary>
    public interface IQuery
    {
    }

    /// <summary>
    /// Marker with a return type <typeparamref name="T"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IQuery<T> : IQuery
    {
    }
}
