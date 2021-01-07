using Chatter.MessageBrokers.Reliability.EntityFramework;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace CarRental.Infrastructure.Repositories.Contexts
{
    public class CarRentalContext : DbContext
    {
        public CarRentalContext(DbContextOptions<CarRentalContext> options) : base(options)
        {
        }

        /// <summary>
        ///     Called when [model creating].
        /// </summary>
        /// <param name="modelBuilder">The model builder.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly())
                        .ApplyConfigurationsFromAssembly(typeof(OutboxMessageConfiguration).Assembly);

            base.OnModelCreating(modelBuilder);
        }
    }
}
