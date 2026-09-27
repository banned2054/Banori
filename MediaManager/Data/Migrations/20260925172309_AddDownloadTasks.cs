using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DownloadTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FeedItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TorrentFile = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TorrentUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    SavePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    NamingSnapshot = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Episode = table.Column<decimal>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsUserPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: true),
                    TotalSize = table.Column<long>(type: "INTEGER", nullable: true),
                    ReplacesTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReplacedByTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailedStep = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadTasks_FeedItems_FeedItemId",
                        column: x => x.FeedItemId,
                        principalTable: "FeedItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DownloadTasks_MediaSubjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "MediaSubjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTasks_FeedItemId",
                table: "DownloadTasks",
                column: "FeedItemId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTasks_InfoHash",
                table: "DownloadTasks",
                column: "InfoHash",
                unique: true,
                filter: "\"Status\" IN ('PendingSubmit', 'Submitting', 'Downloading', 'Paused')");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTasks_SubjectId_Episode_Status",
                table: "DownloadTasks",
                columns: new[] { "SubjectId", "Episode", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadTasks");
        }
    }
}
