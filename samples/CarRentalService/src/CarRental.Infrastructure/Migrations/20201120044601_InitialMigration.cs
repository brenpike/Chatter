using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CarRental.Infrastructure.Migrations
{
    public partial class InitialMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CarRental",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Vendor = table.Column<string>(nullable: false),
                    Airport = table.Column<string>(nullable: false),
                    From = table.Column<DateTime>(nullable: false),
                    Until = table.Column<DateTime>(nullable: false),
                    ReservationId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarRental", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessage",
                columns: table => new
                {
                    MessageId = table.Column<string>(nullable: false),
                    ReceivedByInboxAtUtc = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessage", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<string>(nullable: false),
                    Destination = table.Column<string>(nullable: false),
                    MessageContext = table.Column<string>(nullable: false),
                    MessageBody = table.Column<string>(nullable: false),
                    MessageContentType = table.Column<string>(nullable: false),
                    SentToOutboxAtUtc = table.Column<DateTime>(nullable: false),
                    ProcessedFromOutboxAtUtc = table.Column<DateTime>(nullable: true),
                    BatchId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.Id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CarRental");

            migrationBuilder.DropTable(
                name: "InboxMessage");

            migrationBuilder.DropTable(
                name: "OutboxMessage");
        }
    }
}
