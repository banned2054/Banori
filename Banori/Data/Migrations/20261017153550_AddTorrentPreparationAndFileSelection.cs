using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTorrentPreparationAndFileSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystemPaused",
                table: "DownloadTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PreparationStage",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<long>(
                name: "SelectedTotalBytes",
                table: "DownloadTasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSelectedForDownload",
                table: "DownloadTaskFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectionReason",
                table: "DownloadTaskFiles",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSystemPaused",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "PreparationStage",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "SelectedTotalBytes",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "IsSelectedForDownload",
                table: "DownloadTaskFiles");

            migrationBuilder.DropColumn(
                name: "SelectionReason",
                table: "DownloadTaskFiles");
        }
    }
}
