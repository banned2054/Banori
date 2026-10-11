#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace MediaManager.Data.Migrations.MySql;

/// <inheritdoc />
public partial class UnlimitNamingIngredients : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>("NamingIngredients", "FeedItems", "longtext", nullable : true,
                                             oldClrType : typeof(string), oldType : "varchar(1000)",
                                             oldMaxLength : 1000, oldNullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");
        migrationBuilder.AlterColumn<string>("NamingIngredients", "DownloadTasks", "longtext", nullable : true,
                                             oldClrType : typeof(string), oldType : "varchar(1000)",
                                             oldMaxLength : 1000, oldNullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>("NamingIngredients", "FeedItems", "varchar(1000)", maxLength : 1000,
                                             nullable : true, oldClrType : typeof(string), oldType : "longtext",
                                             oldNullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");
        migrationBuilder.AlterColumn<string>("NamingIngredients", "DownloadTasks", "varchar(1000)", maxLength : 1000,
                                             nullable : true, oldClrType : typeof(string), oldType : "longtext",
                                             oldNullable : true)
                        .Annotation("MySql:CharSet", "utf8mb4");
    }
}
