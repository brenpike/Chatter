using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;

namespace CarRental.Infrastructure.Repositories.Configurations
{
    public class CarRentalConfiguration : IEntityTypeConfiguration<Domain.Aggregates.CarRental>
    {
        public void Configure(EntityTypeBuilder<Domain.Aggregates.CarRental> builder)
        {
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Id).IsRequired();

            builder.Property<string>("_airport")
                .HasColumnName("Airport").IsRequired();

            builder.Property<DateTime>("_from")
                .HasColumnName("From").IsRequired();

            builder.Property<DateTime>("_until")
                .HasColumnName("Until").IsRequired();

            builder.Property<string>("_vendor")
                .HasColumnName("Vendor").IsRequired();

            builder.Property<Guid>("_reservationId")
                .HasColumnName("ReservationId").IsRequired();
        }
    }
}
