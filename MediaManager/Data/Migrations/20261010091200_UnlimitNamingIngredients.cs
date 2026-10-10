using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaManager.Data.Migrations;

/// <inheritdoc />
public partial class UnlimitNamingIngredients : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "NamingIngredients", table: "FeedItems", type: "TEXT", nullable: true,
            oldClrType: typeof(string), oldType: "TEXT", oldMaxLength: 1000, oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "NamingIngredients", table: "DownloadTasks", type: "TEXT", nullable: true,
            oldClrType: typeof(string), oldType: "TEXT", oldMaxLength: 1000, oldNullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "NamingIngredients", table: "FeedItems", type: "TEXT", maxLength: 1000, nullable: true,
            oldClrType: typeof(string), oldType: "TEXT", oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "NamingIngredients", table: "DownloadTasks", type: "TEXT", maxLength: 1000, nullable: true,
            oldClrType: typeof(string), oldType: "TEXT", oldNullable: true);
    }
}
