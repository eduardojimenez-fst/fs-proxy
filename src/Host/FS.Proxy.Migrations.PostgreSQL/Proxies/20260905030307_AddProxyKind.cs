using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FS.Proxy.Migrations.PostgreSQL.Proxies
{
    /// <inheritdoc />
    public partial class AddProxyKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                schema: "proxies",
                table: "Proxies",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "proxies",
                table: "Proxies");
        }
    }
}
