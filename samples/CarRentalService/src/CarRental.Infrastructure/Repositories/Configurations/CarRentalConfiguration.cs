using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Repositories.Configurations
{
    public class CarRentalConfiguration : IEntityTypeConfiguration<Domain.Aggregates.CarRental>
    {
        public void Configure(EntityTypeBuilder<Domain.Aggregates.CarRental> builder)
        {
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Id).IsRequired();

            builder.Property(t => t.Airport).IsRequired();

            builder.Property(t => t.From).IsRequired();

            builder.Property(t => t.Until).IsRequired();

            builder.Property(t => t.Vendor).IsRequired();

            builder.Property(t => t.ReservationId).IsRequired();
        }
    }
}
