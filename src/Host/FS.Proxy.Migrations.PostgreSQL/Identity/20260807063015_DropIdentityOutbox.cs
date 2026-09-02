using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FS.Proxy.Migrations.PostgreSQL.Identity
{
    /// <inheritdoc />
    public partial class DropIdentityOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Carry the existing outbox/inbox across to the framework schema before dropping the
            // identity-owned tables. Unprocessed outbox rows would otherwise be lost (they are
            // pending integration events), and inbox rows must survive or already-handled events
            // would be reprocessed. Ordering across contexts is not guaranteed by EF, so this
            // guards on the destination existing: when the framework schema has not been created
            // yet the copy no-ops, which is correct for a fresh database.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('framework."OutboxMessages"') IS NOT NULL
                       AND to_regclass('identity."OutboxMessages"') IS NOT NULL THEN
                        INSERT INTO framework."OutboxMessages"
                            ("Id", "CreatedOnUtc", "Type", "Payload", "TenantId",
                             "CorrelationId", "ProcessedOnUtc", "RetryCount", "LastError",
                             "IsDead", "NextRetryAt")
                        SELECT "Id", "CreatedOnUtc", "Type", "Payload", "TenantId",
                               "CorrelationId", "ProcessedOnUtc", "RetryCount", "LastError",
                               "IsDead", "NextRetryAt"
                        FROM identity."OutboxMessages"
                        ON CONFLICT ("Id") DO NOTHING;
                    END IF;

                    IF to_regclass('framework."InboxMessages"') IS NOT NULL
                       AND to_regclass('identity."InboxMessages"') IS NOT NULL THEN
                        INSERT INTO framework."InboxMessages"
                            ("Id", "EventType", "HandlerName", "ProcessedOnUtc", "TenantId")
                        SELECT "Id", "EventType", "HandlerName", "ProcessedOnUtc", "TenantId"
                        FROM identity."InboxMessages"
                        ON CONFLICT ("Id", "HandlerName") DO NOTHING;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "identity");
        }

        /// <inheritdoc />
        /// <remarks>
        /// The rollback recreates the identity-owned tables empty — it deliberately does not copy
        /// rows back from the framework schema, which by then also holds messages written by other
        /// modules that identity never owned.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HandlerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EventType = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProcessedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Id, x.HandlerName });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDead = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    NextRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    ProcessedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Type = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });
        }
    }
}
