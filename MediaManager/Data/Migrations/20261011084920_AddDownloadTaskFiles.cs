using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadTaskFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DownloadRootPath",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaDirectory",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingSavePath",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeedDirectory",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DownloadTaskFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EpisodeNumberOverride = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    EpisodeEndOverride = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    AnimeRelativePathOverride = table.Column<string>(type: "TEXT", nullable: true),
                    IsManualOverride = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClassificationReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    PendingRenameFrom = table.Column<string>(type: "TEXT", nullable: true),
                    PendingRenameTo = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadTaskFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadTaskFiles_DownloadTasks_DownloadTaskId",
                        column: x => x.DownloadTaskId,
                        principalTable: "DownloadTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTaskFiles_DownloadTaskId_FileIndex",
                table: "DownloadTaskFiles",
                columns: new[] { "DownloadTaskId", "FileIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadTaskFiles");

            migrationBuilder.DropColumn(
                name: "DownloadRootPath",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "MediaDirectory",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "PendingSavePath",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "SeedDirectory",
                table: "DownloadTasks");
        }
    }
}
