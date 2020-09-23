using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace Samples.SharedKernel.Interfaces
{
    /// <summary>
    /// A contract defining read operations for a repository 
    /// </summary>
    /// <typeparam name="TEntity">The entity type operations will apply to</typeparam>
    /// <typeparam name="TId">The type for entity identifier</typeparam>
    public interface IReadOnlyRepository<TEntity, TId>
    {
        /// <summary>
        /// Get an entity by it's identifier
        /// </summary>
        /// <param name="id"></param>
        /// <returns>Will return null if entity does not exist or an instance of the entity</returns>
        TEntity GetById(TId id);

        /// <summary>
        /// Gets a list of all entities that matching an filter condition if one is specified
        /// </summary>
        /// <param name="func">An optional delegate that can be used to apply a filter condition</param>
        /// <returns>Will return an empty enumeration if no matches or a list of entities</returns>
        Task<IEnumerable<TEntity>> GetAll(Func<IQueryable<TEntity>, IQueryable<TEntity>> func = null);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="predicate"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        Task<TEntity> FindFirst(Expression<Func<TEntity, bool>> predicate, Func<IQueryable<TEntity>, IQueryable<TEntity>> func = null);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="predicate"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> Find(Expression<Func<TEntity, bool>> predicate, Func<IQueryable<TEntity>, IQueryable<TEntity>> func = null);
    }
}
