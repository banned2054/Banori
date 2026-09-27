using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMikanReadOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaSubjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    NameCn = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AirDate = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CachedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaSubjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FeedUrlProtected = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RefreshIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    FixedSubjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastRefreshStartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastRefreshCompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    NextRefreshAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubjectTitleMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NormalizedTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SubjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubjectTitleMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubjectTitleMappings_MediaSubjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "MediaSubjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StableId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourceItemId = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Link = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TorrentUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EvaluatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ParsedTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Episode = table.Column<decimal>(type: "TEXT", nullable: true),
                    StartEpisode = table.Column<int>(type: "INTEGER", nullable: true),
                    EndEpisode = table.Column<int>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    GroupType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Language = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SubtitleType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    WebSource = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    VideoCodec = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AudioCodec = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ColorBitDepth = table.Column<int>(type: "INTEGER", nullable: true),
                    SubjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    DecisionStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Preference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    NamingPreview = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedItems_MediaSubjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "MediaSubjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FeedItems_MediaSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "MediaSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefreshWorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshWorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshWorkItems_MediaSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "MediaSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_SubjectId_Episode_Status",
                table: "FeedItems",
                columns: new[] { "SubjectId", "Episode", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_SubscriptionId_StableId",
                table: "FeedItems",
                columns: new[] { "SubscriptionId", "StableId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshWorkItems_SubscriptionId_Status",
                table: "RefreshWorkItems",
                columns: new[] { "SubscriptionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SubjectTitleMappings_NormalizedTitle",
                table: "SubjectTitleMappings",
                column: "NormalizedTitle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubjectTitleMappings_SubjectId",
                table: "SubjectTitleMappings",
                column: "SubjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedItems");

            migrationBuilder.DropTable(
                name: "RefreshWorkItems");

            migrationBuilder.DropTable(
                name: "SubjectTitleMappings");

            migrationBuilder.DropTable(
                name: "MediaSubscriptions");

            migrationBuilder.DropTable(
                name: "MediaSubjects");
        }
    }
}
