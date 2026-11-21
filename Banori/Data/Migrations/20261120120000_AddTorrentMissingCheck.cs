using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextMissingCheckAtUtc",
                table: "DownloadTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissingCheckPendingAtUtc",
                table: "DownloadTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MissingCheckConfigKey",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissingCleanupConfirmedAtUtc",
                table: "DownloadTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MissingCleanupScopeKey",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MissingCleanupReplacedById",
                table: "DownloadTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingSubmitSavePath",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

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
