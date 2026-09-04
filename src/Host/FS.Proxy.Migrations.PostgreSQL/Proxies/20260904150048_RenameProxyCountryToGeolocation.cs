using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FS.Proxy.Migrations.PostgreSQL.Proxies
{
    /// <inheritdoc />
    public partial class RenameProxyCountryToGeolocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Country",
                schema: "proxies",
                table: "Proxies",
                newName: "Geolocation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Geolocation",
                schema: "proxies",
                table: "Proxies",
                newName: "Country");
        }
    }
}
