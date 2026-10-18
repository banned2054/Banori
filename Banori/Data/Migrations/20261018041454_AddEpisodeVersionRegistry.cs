using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodeVersionRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SelectionSyncPending",
                table: "DownloadTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByTaskId",
                table: "DownloadTaskFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EpisodeVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Episode = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IncumbentVideoFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PendingVideoFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SupersededVideoFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpisodeVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeVersions_SubjectId_Episode",
                table: "EpisodeVersions",
                columns: new[] { "SubjectId", "Episode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpisodeVersions");

            migrationBuilder.DropColumn(
                name: "SelectionSyncPending",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "SupersededByTaskId",
                table: "DownloadTaskFiles");
        }
    }
}
