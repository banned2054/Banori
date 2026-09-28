using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProxyServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProxyId",
                table: "MediaSubscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProxyServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PasswordProtected = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxyServers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaSubscriptions_ProxyId",
                table: "MediaSubscriptions",
                column: "ProxyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProxyServers_Name",
                table: "ProxyServers",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaSubscriptions_ProxyServers_ProxyId",
                table: "MediaSubscriptions",
                column: "ProxyId",
                principalTable: "ProxyServers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaSubscriptions_ProxyServers_ProxyId",
                table: "MediaSubscriptions");

            migrationBuilder.DropTable(
                name: "ProxyServers");

            migrationBuilder.DropIndex(
                name: "IX_MediaSubscriptions_ProxyId",
                table: "MediaSubscriptions");

            migrationBuilder.DropColumn(
                name: "ProxyId",
                table: "MediaSubscriptions");
        }
    }
}
