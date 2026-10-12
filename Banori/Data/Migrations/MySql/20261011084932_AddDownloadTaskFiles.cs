#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Banori.Data.Migrations.MySql;

/// <inheritdoc />
public partial class AddDownloadTaskFiles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
                                           "DownloadRootPath",
                                           "DownloadTasks",
                                           "varchar(1000)",
                                           maxLength : 1000,
                                           nullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.AddColumn<string>(
                                           "MediaDirectory",
                                           "DownloadTasks",
                                           "varchar(1000)",
                                           maxLength : 1000,
                                           nullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.AddColumn<string>(
                                           "PendingSavePath",
                                           "DownloadTasks",
                                           "varchar(1000)",
                                           maxLength : 1000,
                                           nullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.AddColumn<string>(
                                           "SeedDirectory",
                                           "DownloadTasks",
                                           "varchar(1000)",
                                           maxLength : 1000,
                                           nullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "DownloadTaskFiles",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         DownloadTaskId =
                                             table.Column<Guid>("char(36)", nullable : false,
                                                                collation : "ascii_general_ci"),
                                         FileIndex = table.Column<int>("int", nullable : false),
                                         OriginalRelativePath = table.Column<string>("longtext", nullable : false)
                                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         CurrentRelativePath = table.Column<string>("longtext", nullable : false)
                                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         Size = table.Column<long>("bigint", nullable : false),
                                         Kind = table.Column<string>("varchar(32)", maxLength : 32, nullable : false)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         EpisodeNumberOverride =
                                             table.Column<decimal>("decimal(18,4)", precision : 18, scale : 4,
                                                                   nullable : true),
                                         EpisodeEndOverride =
                                             table.Column<decimal>("decimal(18,4)", precision : 18, scale : 4,
                                                                   nullable : true),
                                         AnimeRelativePathOverride = table.Column<string>("longtext", nullable : true)
                                                                          .Annotation("MySql:CharSet", "utf8mb4"),
                                         IsManualOverride = table.Column<bool>("tinyint(1)", nullable : false),
                                         ClassificationReason = table
                                                               .Column<string>("varchar(1000)", maxLength : 1000,
                                                                               nullable : false)
                                                               .Annotation("MySql:CharSet", "utf8mb4"),
                                         PendingRenameFrom = table.Column<string>("longtext", nullable : true)
                                                                  .Annotation("MySql:CharSet", "utf8mb4"),
                                         PendingRenameTo = table.Column<string>("longtext", nullable : true)
                                                                .Annotation("MySql:CharSet", "utf8mb4"),
                                         UpdatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false)
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_DownloadTaskFiles", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_DownloadTaskFiles_DownloadTasks_DownloadTaskId",
                                                          x => x.DownloadTaskId,
                                                          "DownloadTasks",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
                                     "IX_DownloadTaskFiles_DownloadTaskId_FileIndex",
                                     "DownloadTaskFiles",
                                     new[] { "DownloadTaskId", "FileIndex" },
                                     unique : true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
                                   "DownloadTaskFiles");

        migrationBuilder.DropColumn(
                                    "DownloadRootPath",
                                    "DownloadTasks");

        migrationBuilder.DropColumn(
                                    "MediaDirectory",
                                    "DownloadTasks");

        migrationBuilder.DropColumn(
                                    "PendingSavePath",
                                    "DownloadTasks");

        migrationBuilder.DropColumn(
                                    "SeedDirectory",
                                    "DownloadTasks");
    }
}
