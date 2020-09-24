using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace CarRental.Infrastructure.Repositories.Contexts
{
    public class CarRentalContext : DbContext
    {
        public CarRentalContext(DbContextOptions<CarRentalContext> options) : base(options)
        {
        }

        //public static readonly ILoggerFactory MyLoggerFactory
        //    = LoggerFactory.Create(builder =>
        //    {
        //        builder.AddFilter((category, level) =>
        //              (category == DbLoggerCategory.Database.Transaction.Name ||
        //              category == DbLoggerCategory.Database.Connection.Name ||
        //              category == DbLoggerCategory.Update.Name ||
        //              category == DbLoggerCategory.Database.Command.Name ||
        //              category == DbLoggerCategory.Query.Name ||
        //              category == DbLoggerCategory.Infrastructure.Name ||
        //              category == DbLoggerCategory.Model.Name)
        //              && (level == LogLevel.Trace ||
        //              level == LogLevel.Debug ||
        //              level == LogLevel.Information ||
        //              level == LogLevel.Error ||
        //              level == LogLevel.Critical ||
        //              level == LogLevel.Warning))
        //          .AddConsole();
        //    });

        DbSet<Domain.Aggregates.CarRental> CarRentals { get; set; }
        DbSet<OutboxMessage> OutboxMessages { get; set; }
        DbSet<InboxMessage> InboxMessages { get; set; }

        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (optionsBuilder.IsConfigured)
            {
                return;
            }

            //TODO: add connstring to config
            optionsBuilder.UseSqlServer(
                @"Data Source=DESKTOP-6D5GE0I\SQLEXPRESS;Database=CarRentals;Integrated Security=True;Connect Timeout=30;Encrypt=False;TrustServerCertificate=False;ApplicationIntent=ReadWrite;MultiSubnetFailover=False",
                b => b.MigrationsAssembly(typeof(CarRentalContext).Assembly.FullName).EnableRetryOnFailure(5));
            base.OnConfiguring(optionsBuilder);
        }

        /// <summary>
        ///     Called when [model creating].
        /// </summary>
        /// <param name="modelBuilder">The model builder.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
            base.OnModelCreating(modelBuilder);
        }
    }
}
