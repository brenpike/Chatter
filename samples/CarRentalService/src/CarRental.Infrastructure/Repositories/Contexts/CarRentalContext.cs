using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.EntityFrameworkCore;
using System;
using System.Reflection;

namespace CarRental.Infrastructure.Repositories.Contexts
{
    public class CarRentalContext : DbContext
    {
        private readonly ReliabilityOptions _reliabilityOptions;

        public CarRentalContext(DbContextOptions<CarRentalContext> options, ReliabilityOptions reliabilityOptions) : base(options)
        {
            _reliabilityOptions = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));
        }

        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (optionsBuilder.IsConfigured)
            {
                return;
            }

            var connStr = _reliabilityOptions.Persistance?.ConnectionString;
            if (string.IsNullOrWhiteSpace(connStr))
            {
                throw new ArgumentNullException(nameof(connStr), "A connection string is required. Consider adding 'Chatter:MessageBrokers:Reliability:ConnectionString'.");
            }

            optionsBuilder.UseSqlServer(
                connStr,
                b => b.MigrationsAssembly(typeof(CarRentalContext).Assembly.FullName).EnableRetryOnFailure(5));
            base.OnConfiguring(optionsBuilder);
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
