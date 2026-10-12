using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCandidateSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectionOutcome",
                table: "FeedItems",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "CandidateSelections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Episode = table.Column<decimal>(type: "TEXT", nullable: false),
                    WinnerFeedItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RankingBasis = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    NamingSnapshot = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IntentTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IntentFailedReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SelectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateSelections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CandidateSelections_FeedItems_WinnerFeedItemId",
                        column: x => x.WinnerFeedItemId,
                        principalTable: "FeedItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CandidateSelections_MediaSubjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "MediaSubjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateSelections_IntentTaskId",
                table: "CandidateSelections",
                column: "IntentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateSelections_SubjectId_MediaType_Episode",
                table: "CandidateSelections",
                columns: new[] { "SubjectId", "MediaType", "Episode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateSelections_WinnerFeedItemId",
                table: "CandidateSelections",
                column: "WinnerFeedItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateSelections");

            migrationBuilder.DropColumn(
                name: "SelectionOutcome",
                table: "FeedItems");
        }
    }
}
