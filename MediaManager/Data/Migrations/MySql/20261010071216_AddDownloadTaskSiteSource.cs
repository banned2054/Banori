#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace MediaManager.Data.Migrations.MySql;

/// <inheritdoc />
public partial class AddDownloadTaskSiteSource : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
                                           "SiteSource",
                                           "DownloadTasks",
                                           "varchar(50)",
                                           maxLength : 50,
                                           nullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
                                    "SiteSource",
                                    "DownloadTasks");
    }
}
