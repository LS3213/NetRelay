using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRelay.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleasePackageDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "package_deleted_at",
                table: "releases",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "package_deleted_at",
                table: "releases");
        }
    }
}
