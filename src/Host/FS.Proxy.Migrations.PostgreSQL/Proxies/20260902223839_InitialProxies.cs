using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FS.Proxy.Migrations.PostgreSQL.Proxies
{
    /// <inheritdoc />
    public partial class InitialProxies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "proxies");

            migrationBuilder.CreateTable(
                name: "ApiClients",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthCheckTargets",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TestUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ExpectedStatusCode = table.Column<int>(type: "integer", nullable: true),
                    ExpectedBodyKeyword = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TimeoutMs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PolicyProfiles",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    FailureThreshold = table.Column<int>(type: "integer", nullable: false),
                    WindowMinutes = table.Column<int>(type: "integer", nullable: false),
                    MinDistinctReporters = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderAccounts",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderType = table.Column<int>(type: "integer", nullable: false),
                    ProtectedCredentials = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ConsecutiveSyncFailures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Proxies",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Protocol = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRenewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proxies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Proxies_ProviderAccounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalSchema: "proxies",
                        principalTable: "ProviderAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TagHealthCheckTargetAssignments",
                schema: "proxies",
                columns: table => new
                {
                    TagId = table.Column<Guid>(type: "uuid", nullable: false),
                    HealthCheckTargetId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagHealthCheckTargetAssignments", x => x.TagId);
                    table.ForeignKey(
                        name: "FK_TagHealthCheckTargetAssignments_HealthCheckTargets_HealthCh~",
                        column: x => x.HealthCheckTargetId,
                        principalSchema: "proxies",
                        principalTable: "HealthCheckTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TagHealthCheckTargetAssignments_Tags_TagId",
                        column: x => x.TagId,
                        principalSchema: "proxies",
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagPolicyAssignments",
                schema: "proxies",
                columns: table => new
                {
                    TagId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyProfileId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagPolicyAssignments", x => x.TagId);
                    table.ForeignKey(
                        name: "FK_TagPolicyAssignments_PolicyProfiles_PolicyProfileId",
                        column: x => x.PolicyProfileId,
                        principalSchema: "proxies",
                        principalTable: "PolicyProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TagPolicyAssignments_Tags_TagId",
                        column: x => x.TagId,
                        principalSchema: "proxies",
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProxyTagAssignments",
                schema: "proxies",
                columns: table => new
                {
                    ProxyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxyTagAssignments", x => new { x.ProxyId, x.TagId });
                    table.ForeignKey(
                        name: "FK_ProxyTagAssignments_Proxies_ProxyId",
                        column: x => x.ProxyId,
                        principalSchema: "proxies",
                        principalTable: "Proxies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProxyTagAssignments_Tags_TagId",
                        column: x => x.TagId,
                        principalSchema: "proxies",
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProxyUsageEvents",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProxyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    HealthCheckTargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportedByApiClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxyUsageEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxyUsageEvents_Proxies_ProxyId",
                        column: x => x.ProxyId,
                        principalSchema: "proxies",
                        principalTable: "Proxies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiClients_ApiKeyHash",
                schema: "proxies",
                table: "ApiClients",
                column: "ApiKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_ProviderType",
                schema: "proxies",
                table: "ProviderAccounts",
                column: "ProviderType");

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_ProviderAccountId_ExternalId",
                schema: "proxies",
                table: "Proxies",
                columns: new[] { "ProviderAccountId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_Status",
                schema: "proxies",
                table: "Proxies",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ProxyTagAssignments_TagId",
                schema: "proxies",
                table: "ProxyTagAssignments",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ProxyUsageEvents_ProxyId_OccurredAtUtc",
                schema: "proxies",
                table: "ProxyUsageEvents",
                columns: new[] { "ProxyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TagHealthCheckTargetAssignments_HealthCheckTargetId",
                schema: "proxies",
                table: "TagHealthCheckTargetAssignments",
                column: "HealthCheckTargetId");

            migrationBuilder.CreateIndex(
                name: "IX_TagPolicyAssignments_PolicyProfileId",
                schema: "proxies",
                table: "TagPolicyAssignments",
                column: "PolicyProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                schema: "proxies",
                table: "Tags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiClients",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "ProxyTagAssignments",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "ProxyUsageEvents",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "TagHealthCheckTargetAssignments",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "TagPolicyAssignments",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "Proxies",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "HealthCheckTargets",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "PolicyProfiles",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "Tags",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "ProviderAccounts",
                schema: "proxies");
        }
    }
}
