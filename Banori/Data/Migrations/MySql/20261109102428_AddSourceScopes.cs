using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Banori.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddSourceScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EpisodeVersions_SubjectId_Episode",
                table: "EpisodeVersions");

            migrationBuilder.DropIndex(
                name: "IX_CandidateSelections_SubjectId_MediaType_Episode",
                table: "CandidateSelections");

            migrationBuilder.AddColumn<string>(
                name: "SourceScope",
                table: "FeedItems",
                type: "varchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "(legacy)")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SourceScope",
                table: "EpisodeVersions",
                type: "varchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "(legacy)")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SourceScope",
                table: "CandidateSelections",
                type: "varchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "(legacy)")
                .Annotation("MySql:CharSet", "utf8mb4");

            // 存量任务来源归一：旧上传兜底 bt 归 other，已知标签与任意非空值统一 trim + 小写。
            // 反引号标识符同时被 SQLite 与 MySQL 接受；不修改 NULL（NULL 仍表示未知 RSS，读取时归 (unknown)）。
            // 第二条不比较大小写：MySQL 默认 collation 不区分大小写，条件式比较会漏掉 "U2" 这类旧值。
            migrationBuilder.Sql("""
                                 UPDATE `DownloadTasks` SET `SiteSource` = 'other'
                                  WHERE LOWER(TRIM(`SiteSource`)) = 'bt';
                                 UPDATE `DownloadTasks` SET `SiteSource` = LOWER(TRIM(`SiteSource`))
                                  WHERE `SiteSource` IS NOT NULL;
                                 """);

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_SubjectId_MediaType_Episode_SourceScope",
                table: "FeedItems",
                columns: new[] { "SubjectId", "MediaType", "Episode", "SourceScope" });

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeVersions_SubjectId_Episode_SourceScope",
                table: "EpisodeVersions",
                columns: new[] { "SubjectId", "Episode", "SourceScope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateSelections_SubjectId_MediaType_Episode_SourceScope",
                table: "CandidateSelections",
                columns: new[] { "SubjectId", "MediaType", "Episode", "SourceScope" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回退恢复旧唯一索引：升级后若已写入同一组的两个来源行，旧索引会因重复键失败。
            // Down 只保证迁移后尚未写入新来源行的干净回退；生产回退必须先备份（或接受手工清理重复行）。
            migrationBuilder.DropIndex(
                name: "IX_FeedItems_SubjectId_MediaType_Episode_SourceScope",
                table: "FeedItems");

            migrationBuilder.DropIndex(
                name: "IX_EpisodeVersions_SubjectId_Episode_SourceScope",
                table: "EpisodeVersions");

            migrationBuilder.DropIndex(
                name: "IX_CandidateSelections_SubjectId_MediaType_Episode_SourceScope",
                table: "CandidateSelections");

            migrationBuilder.DropColumn(
                name: "SourceScope",
                table: "FeedItems");

            migrationBuilder.DropColumn(
                name: "SourceScope",
                table: "EpisodeVersions");

            migrationBuilder.DropColumn(
                name: "SourceScope",
                table: "CandidateSelections");

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeVersions_SubjectId_Episode",
                table: "EpisodeVersions",
                columns: new[] { "SubjectId", "Episode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateSelections_SubjectId_MediaType_Episode",
                table: "CandidateSelections",
                columns: new[] { "SubjectId", "MediaType", "Episode" },
                unique: true);
        }
    }
}
