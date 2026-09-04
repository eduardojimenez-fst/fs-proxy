using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FS.Proxy.Migrations.PostgreSQL.Proxies
{
    /// <inheritdoc />
    public partial class AddProxyCountryAndProviderGrouping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Country",
                schema: "proxies",
                table: "Proxies",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderGrouping",
                schema: "proxies",
                table: "Proxies",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Country",
                schema: "proxies",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "ProviderGrouping",
                schema: "proxies",
                table: "Proxies");
        }
    }
}
