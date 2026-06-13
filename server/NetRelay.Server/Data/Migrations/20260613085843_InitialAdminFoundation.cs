using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRelay.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialAdminFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "admin_accounts",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    singleton_key = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    username = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    password_hash = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protected_totp_secret = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    failed_login_count = table.Column<int>(type: "int", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_accounts", x => x.id);
                    table.CheckConstraint("ck_admin_accounts_singleton", "`singleton_key` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    admin_account_id = table.Column<byte[]>(type: "binary(16)", nullable: true),
                    action = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    result = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    request_id = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    target_type = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    target_id = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    details_json = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    previous_hash = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    entry_hash = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    occurred_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "admin_login_challenges",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    admin_account_id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_login_challenges", x => x.id);
                    table.ForeignKey(
                        name: "FK_admin_login_challenges_admin_accounts_admin_account_id",
                        column: x => x.admin_account_id,
                        principalTable: "admin_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "admin_sessions",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    admin_account_id = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    session_token_hash = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    csrf_token_hash = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    reauthenticated_until = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_admin_sessions_admin_accounts_admin_account_id",
                        column: x => x.admin_account_id,
                        principalTable: "admin_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_admin_accounts_singleton_key",
                table: "admin_accounts",
                column: "singleton_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_accounts_username",
                table: "admin_accounts",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_login_challenges_admin_account_id",
                table: "admin_login_challenges",
                column: "admin_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_admin_login_challenges_expires_at",
                table: "admin_login_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_admin_account_id",
                table: "admin_sessions",
                column: "admin_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_expires_at",
                table: "admin_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_session_token_hash",
                table: "admin_sessions",
                column: "session_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_action_occurred_at",
                table: "audit_logs",
                columns: new[] { "action", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_occurred_at",
                table: "audit_logs",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_login_challenges");

            migrationBuilder.DropTable(
                name: "admin_sessions");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "admin_accounts");
        }
    }
}
