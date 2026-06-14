using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRelay.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceBlocksAndPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_blocks",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    device_id = table.Column<byte[]>(type: "binary(16)", nullable: true),
                    installation_id = table.Column<byte[]>(type: "binary(16)", nullable: true),
                    reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_blocks", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_blocks_device_installations_installation_id",
                        column: x => x.installation_id,
                        principalTable: "device_installations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_device_blocks_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "global_policies",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    type = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    target_version_min = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    target_version_max = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    allow_update = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    status = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_policies", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_device_blocks_device_id",
                table: "device_blocks",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_blocks_expires_at",
                table: "device_blocks",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_device_blocks_installation_id",
                table: "device_blocks",
                column: "installation_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_blocks_status",
                table: "device_blocks",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_global_policies_expires_at",
                table: "global_policies",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_global_policies_status",
                table: "global_policies",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_global_policies_type",
                table: "global_policies",
                column: "type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_blocks");

            migrationBuilder.DropTable(
                name: "global_policies");
        }
    }
}
