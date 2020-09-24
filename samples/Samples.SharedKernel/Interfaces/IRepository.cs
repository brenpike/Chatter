using System.Threading.Tasks;

namespace Samples.SharedKernel.Interfaces
{
    /// <summary>
    /// A contract defining write operations for a repository 
    /// </summary>
    /// <typeparam name="TEntity">The entity type operations will apply to</typeparam>
    /// <typeparam name="TId">The type for entity identifier</typeparam>
    public interface IRepository<TEntity, TId> : IReadOnlyRepository<TEntity, TId>
    {
        /// <summary>
        /// Will add an entity
        /// </summary>
        /// <param name="entity">The entity to add</param>
        void Add(TEntity entity);

        Task AddAsync(TEntity entity);

        /// <summary>
        ///  Will update an entity
        /// </summary>
        /// <param name="entity">The entity to update</param>
        void Update(TEntity entity);

        /// <summary>
        ///  Will delete an entity
        /// </summary>
        /// <param name="entity">The entity to delete</param>
        void Remove(TEntity entity);

        /// <summary>
        ///  Will add an entity if it does not already exist or will update an existing entity
        /// </summary>
        /// <param name="entity">The entity to add or update</param>
        void AddOrUpdate(TEntity entity);
    }
}
