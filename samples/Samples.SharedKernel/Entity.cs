namespace Samples.SharedKernel
{
    public abstract class Entity<TId>
    {
        public virtual TId Id { get; set; }
    }
}