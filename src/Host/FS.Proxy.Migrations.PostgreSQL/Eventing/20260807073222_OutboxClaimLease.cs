using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FS.Proxy.Migrations.PostgreSQL.Eventing
{
    /// <inheritdoc />
    public partial class OutboxClaimLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                schema: "framework",
                table: "OutboxMessages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedUntilUtc",
                schema: "framework",
                table: "OutboxMessages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                schema: "framework",
                table: "OutboxMessages",
                columns: new[] { "IsDead", "ProcessedOnUtc", "ClaimedUntilUtc", "CreatedOnUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Pending",
                schema: "framework",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                schema: "framework",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "ClaimedUntilUtc",
                schema: "framework",
                table: "OutboxMessages");
        }
    }
}
