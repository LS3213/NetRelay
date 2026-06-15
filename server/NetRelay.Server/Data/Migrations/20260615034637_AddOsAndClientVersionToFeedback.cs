using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRelay.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOsAndClientVersionToFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_version",
                table: "feedbacks",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "os_version",
                table: "feedbacks",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "client_version",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "os_version",
                table: "feedbacks");
        }
    }
}
