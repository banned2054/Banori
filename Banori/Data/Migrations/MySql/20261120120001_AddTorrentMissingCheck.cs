using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddTorrentMissingCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TorrentMissingAtUtc",
                table: "DownloadTasks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextMissingCheckAtUtc",
                table: "DownloadTasks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissingCheckPendingAtUtc",
                table: "DownloadTasks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MissingCheckConfigKey",
                table: "DownloadTasks",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissingCleanupConfirmedAtUtc",
                table: "DownloadTasks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MissingCleanupScopeKey",
                table: "DownloadTasks",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "MissingCleanupReplacedById",
                table: "DownloadTasks",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingSubmitSavePath",
                table: "DownloadTasks",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTasks_NextMissingCheckAtUtc",
                table: "DownloadTasks",
                column: "NextMissingCheckAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadTasks_NextMissingCheckAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "TorrentMissingAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "NextMissingCheckAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "MissingCheckPendingAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "MissingCheckConfigKey",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "MissingCleanupConfirmedAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "MissingCleanupScopeKey",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "MissingCleanupReplacedById",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "PendingSubmitSavePath",
                table: "DownloadTasks");
        }
    }
}
