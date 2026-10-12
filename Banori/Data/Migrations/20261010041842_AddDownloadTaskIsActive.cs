using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadTaskIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadTasks_InfoHash",
                table: "DownloadTasks");

            // SQLite 的 ALTER 只允许添加 VIRTUAL 生成列；虚拟列同样可建唯一索引。
            // 模型中的 stored:true 供 MySQL 基线使用，SQLite 端虚拟/存储的差异对 EF 不可见。
            migrationBuilder.AddColumn<int>(
                name: "ActiveFlag",
                table: "DownloadTasks",
                type: "INTEGER",
                nullable: true,
                computedColumnSql: "CASE WHEN `Status` IN ('PendingSubmit', 'Submitting', 'Downloading', 'Paused') THEN 1 ELSE NULL END");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTasks_InfoHash_ActiveFlag",
                table: "DownloadTasks",
                columns: new[] { "InfoHash", "ActiveFlag" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadTasks_InfoHash_ActiveFlag",
                table: "DownloadTasks");

            migrationBuilder.DropColumn(
                name: "ActiveFlag",
                table: "DownloadTasks");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTasks_InfoHash",
                table: "DownloadTasks",
                column: "InfoHash",
                unique: true,
                filter: "\"Status\" IN ('PendingSubmit', 'Submitting', 'Downloading', 'Paused')");
        }
    }
}
