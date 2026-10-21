using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddDownloadSchedulingAndPathDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSubmitAttemptAtUtc",
                table: "DownloadTasks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextObservationAtUtc",
                table: "DownloadTasks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathDecision",
                table: "DownloadTasks",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "PendingLayoutRequestCount",
                table: "DownloadTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingLayoutRequestedAtUtc",
                table: "DownloadTasks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SelectionRequestCount",
                table: "DownloadTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTasks_NextObservationAtUtc",
                table: "DownloadTasks",
                column: "NextObservationAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadTasks_NextObservationAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "LastSubmitAttemptAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "NextObservationAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "PathDecision",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "PendingLayoutRequestCount",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "PendingLayoutRequestedAtUtc",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "SelectionRequestCount",
                table: "DownloadTasks");
        }
    }
}
