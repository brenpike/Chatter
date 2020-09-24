﻿// <auto-generated />
using System;
using CarRental.Infrastructure.Repositories.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CarRental.Infrastructure.Migrations
{
    [DbContext(typeof(CarRentalContext))]
    partial class CarRentalContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "3.1.8")
                .HasAnnotation("Relational:MaxIdentifierLength", 128)
                .HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn);

            modelBuilder.Entity("CarRental.Domain.Aggregates.CarRental", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uniqueidentifier");

                    b.Property<string>("_airport")
                        .IsRequired()
                        .HasColumnName("Airport")
                        .HasColumnType("nvarchar(max)");

                    b.Property<DateTime>("_from")
                        .HasColumnName("From")
                        .HasColumnType("datetime2");

                    b.Property<Guid>("_reservationId")
                        .HasColumnName("ReservationId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<DateTime>("_until")
                        .HasColumnName("Until")
                        .HasColumnType("datetime2");

                    b.Property<string>("_vendor")
                        .IsRequired()
                        .HasColumnName("Vendor")
                        .HasColumnType("nvarchar(max)");

                    b.HasKey("Id");

                    b.ToTable("CarRentals");
                });

            modelBuilder.Entity("Chatter.MessageBrokers.Reliability.Inbox.InboxMessage", b =>
                {
                    b.Property<string>("MessageId")
                        .HasColumnType("nvarchar(450)");

                    b.Property<DateTime?>("ReceivedByInboxAtUtc")
                        .HasColumnType("datetime2");

                    b.HasKey("MessageId");

                    b.ToTable("InboxMessages");
                });

            modelBuilder.Entity("Chatter.MessageBrokers.Reliability.Outbox.OutboxMessage", b =>
                {
                    b.Property<string>("MessageId")
                        .HasColumnType("nvarchar(450)");

                    b.Property<Guid>("BatchId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<byte[]>("Body")
                        .IsRequired()
                        .HasColumnType("varbinary(max)");

                    b.Property<string>("Destination")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.Property<DateTime?>("ProcessedFromOutboxAtUtc")
                        .HasColumnType("datetime2");

                    b.Property<DateTime>("SentToOutboxAtUtc")
                        .HasColumnType("datetime2");

                    b.Property<string>("StringifiedApplicationProperties")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("StringifiedMessage")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.HasKey("MessageId");

                    b.ToTable("OutboxMessages");
                });
#pragma warning restore 612, 618
        }
    }
}
