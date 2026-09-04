using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FS.Proxy.Migrations.PostgreSQL.Proxies
{
    /// <inheritdoc />
    public partial class AddTagCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TagCategories",
                schema: "proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TagCategoryValues",
                schema: "proxies",
                columns: table => new
                {
                    TagCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_TagCategories_Name",
                schema: "proxies",
                table: "TagCategories",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TagCategoryValues",
                schema: "proxies");

            migrationBuilder.DropTable(
                name: "TagCategories",
                schema: "proxies");
        }
    }
}
