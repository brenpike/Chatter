namespace Samples.SharedKernel.Interfaces
{
    public interface IAggregate<TId>
    {
        TId Id { get; }
    }
}
