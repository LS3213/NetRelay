using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRelay.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceFingerprintToFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "device_id_hash",
                table: "feedbacks",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<byte[]>(
                name: "installation_id",
                table: "feedbacks",
                type: "binary(16)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "device_id_hash",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "installation_id",
                table: "feedbacks");
        }
    }
}
