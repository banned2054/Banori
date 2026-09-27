using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOneShotSubscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOneShot",
                table: "MediaSubscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsOneShot",
                table: "MediaSubscriptions");
        }
    }
}
