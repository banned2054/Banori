using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataVerificationAndArchiveEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArchiveReview",
                table: "DownloadTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsolationState",
                table: "DownloadTasks",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "VerificationIntent",
                table: "DownloadTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationOutcome",
                table: "DownloadTaskFiles",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationPath",
                table: "DownloadTaskFiles",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchiveReview",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "IsolationState",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "VerificationIntent",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "VerificationOutcome",
                table: "DownloadTaskFiles");

            migrationBuilder.DropColumn(
                name: "VerificationPath",
                table: "DownloadTaskFiles");
        }
    }
}
