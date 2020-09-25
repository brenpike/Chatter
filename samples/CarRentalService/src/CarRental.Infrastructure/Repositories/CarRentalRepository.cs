using CarRental.Infrastructure.Repositories.Contexts;
using Microsoft.EntityFrameworkCore;
using Samples.SharedKernel.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace CarRental.Infrastructure.Repositories
{
    public class CarRentalRepository : IRepository<Domain.Aggregates.CarRental, Guid>
    {
        private readonly CarRentalContext _context;

        public CarRentalRepository(CarRentalContext context) 
            => _context = context ?? throw new ArgumentNullException(nameof(context));

        public void Add(Domain.Aggregates.CarRental entity)
        {
            _context.Set<Domain.Aggregates.CarRental>().Add(entity);
        }

        public async Task AddAsync(Domain.Aggregates.CarRental entity)
        {
            await _context.Set<Domain.Aggregates.CarRental>().AddAsync(entity);
            await _context.SaveChangesAsync();
        }

        public void AddOrUpdate(Domain.Aggregates.CarRental entity)
        {
            var exists = _context.Set<Domain.Aggregates.CarRental>().AsNoTracking().Any(x => x.Id.Equals(entity.Id));
            if (exists)
            {
                Update(entity);
            }
            else
            {
                Add(entity);
            }
        }

        public Task<IEnumerable<Domain.Aggregates.CarRental>> Find(Expression<Func<Domain.Aggregates.CarRental, bool>> predicate, Func<IQueryable<Domain.Aggregates.CarRental>, IQueryable<Domain.Aggregates.CarRental>> func = null)
        {
            var result = _context.Set<Domain.Aggregates.CarRental>();
            if (func == null)
                return _context.Set<Domain.Aggregates.CarRental>().Where(predicate)
                .ToListAsync().ContinueWith(t => t.Result.AsEnumerable());

            var resultWithEagerLoading = func(result);
            return resultWithEagerLoading.Where(predicate)
                .ToListAsync().ContinueWith(t => t.Result.AsEnumerable());
        }

        public Task<Domain.Aggregates.CarRental> FindFirst(Expression<Func<Domain.Aggregates.CarRental, bool>> predicate, Func<IQueryable<Domain.Aggregates.CarRental>, IQueryable<Domain.Aggregates.CarRental>> func = null) 
            => Find(predicate, func).ContinueWith(t => t.Result.FirstOrDefault());

        public async Task<IEnumerable<Domain.Aggregates.CarRental>> GetAll(Func<IQueryable<Domain.Aggregates.CarRental>, IQueryable<Domain.Aggregates.CarRental>> func = null)
        {
            var result = _context.Set<Domain.Aggregates.CarRental>();
            if (func == null) return await result.ToListAsync();

            var resultWithEagerLoading = func(result);
            return await resultWithEagerLoading.ToListAsync();
        }

        public Domain.Aggregates.CarRental GetById(Guid id)
            => _context.Set<Domain.Aggregates.CarRental>().Find(id);

        public async Task<Domain.Aggregates.CarRental> GetByIdAsync(Guid id) 
            => await _context.Set<Domain.Aggregates.CarRental>().FindAsync(id);

        public void Remove(Domain.Aggregates.CarRental entity) 
            => _context.Set<Domain.Aggregates.CarRental>().Remove(entity);

        public void Update(Domain.Aggregates.CarRental entity) 
            => _context.Set<Domain.Aggregates.CarRental>().Update(entity);
    }
}
