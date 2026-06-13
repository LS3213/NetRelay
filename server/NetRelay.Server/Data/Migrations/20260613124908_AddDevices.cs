using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRelay.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activation_receipts",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    installation_id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    receipt_id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    key_id = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activation_receipts", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    machine_code = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    device_id_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    fingerprint_version = table.Column<int>(type: "int", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "device_evidence",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    device_id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    category = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    evidence_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    first_seen_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_evidence", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_evidence_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "device_installations",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    device_id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    installation_id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    client_version = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    os_version = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protocol_version = table.Column<int>(type: "int", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_installations", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_installations_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_activation_receipts_expires_at",
                table: "activation_receipts",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_activation_receipts_receipt_id",
                table: "activation_receipts",
                column: "receipt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_evidence_device_id_category_evidence_hash",
                table: "device_evidence",
                columns: new[] { "device_id", "category", "evidence_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_installations_device_id_last_seen_at",
                table: "device_installations",
                columns: new[] { "device_id", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "IX_device_installations_installation_id",
                table: "device_installations",
                column: "installation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_device_id_hash",
                table: "devices",
                column: "device_id_hash");

            migrationBuilder.CreateIndex(
                name: "IX_devices_last_seen_at",
                table: "devices",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "IX_devices_machine_code",
                table: "devices",
                column: "machine_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activation_receipts");

            migrationBuilder.DropTable(
                name: "device_evidence");

            migrationBuilder.DropTable(
                name: "device_installations");

            migrationBuilder.DropTable(
                name: "devices");
        }
    }
}
