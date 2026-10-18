using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations.MySql
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
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "IsolationState",
                table: "DownloadTasks",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "None")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "VerificationIntent",
                table: "DownloadTasks",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "VerificationOutcome",
                table: "DownloadTaskFiles",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "VerificationPath",
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
