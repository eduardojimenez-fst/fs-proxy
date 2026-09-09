using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FS.Proxy.Migrations.MSSQL.Proxies
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TestUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ExpectedStatusCode = table.Column<int>(type: "int", nullable: true),
                    ExpectedBodyKeyword = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TimeoutMs = table.Column<int>(type: "int", nullable: false)
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    FailureThreshold = table.Column<int>(type: "int", nullable: false),
                    WindowMinutes = table.Column<int>(type: "int", nullable: false),
                    MinDistinctReporters = table.Column<int>(type: "int", nullable: false)
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderType = table.Column<int>(type: "int", nullable: false),
                    ProtectedCredentials = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ConsecutiveSyncFailures = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TagCategories",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    Protocol = table.Column<int>(type: "int", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Geolocation = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ProviderGrouping = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRenewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
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
                name: "TagCategoryValues",
                schema: "proxies",
                columns: table => new
                {
                    TagCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagCategoryValues", x => new { x.TagCategoryId, x.Value });
                    table.ForeignKey(
                        name: "FK_TagCategoryValues_TagCategories_TagCategoryId",
                        column: x => x.TagCategoryId,
                        principalSchema: "proxies",
                        principalTable: "TagCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagHealthCheckTargetAssignments",
                schema: "proxies",
                columns: table => new
                {
                    TagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HealthCheckTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagHealthCheckTargetAssignments", x => x.TagId);
                    table.ForeignKey(
                        name: "FK_TagHealthCheckTargetAssignments_HealthCheckTargets_HealthCheckTargetId",
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
                    TagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
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
                    ProxyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProxyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    HealthCheckTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReportedByApiClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                name: "IX_TagCategories_Name",
                schema: "proxies",
                table: "TagCategories",
                column: "Name",
                unique: true);

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
                name: "TagCategoryValues",
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
                name: "TagCategories",
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
