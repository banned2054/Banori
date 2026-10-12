#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Banori.Data.Migrations.MySql;

/// <inheritdoc />
public partial class AddNamingIngredients : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
                                           "NamingIngredients",
                                           "FeedItems",
                                           "varchar(1000)",
                                           maxLength : 1000,
                                           nullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.AddColumn<string>(
                                           "NamingIngredients",
                                           "DownloadTasks",
                                           "varchar(1000)",
                                           maxLength : 1000,
                                           nullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
                                    "NamingIngredients",
                                    "FeedItems");

        migrationBuilder.DropColumn(
                                    "NamingIngredients",
                                    "DownloadTasks");
    }
}
