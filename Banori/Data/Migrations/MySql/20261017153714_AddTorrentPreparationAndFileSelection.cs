using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations.MySql
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
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PreparationStage",
                table: "DownloadTasks",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "None")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "SelectedTotalBytes",
                table: "DownloadTasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSelectedForDownload",
                table: "DownloadTaskFiles",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectionReason",
                table: "DownloadTaskFiles",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
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
