using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOneShotSubscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite 可能未启用外键级联，相关数据一律显式清理，顺序保证引用先于被引用方删除。

            // 1. DownloadTasks.FeedItemId 级联删除：先把一次性订阅条目产生的任务脱离来源，避免连带丢失任务。
            migrationBuilder.Sql("""
                                 UPDATE "DownloadTasks"
                                 SET "FeedItemId" = NULL
                                 WHERE "FeedItemId" IN (
                                     SELECT fi."Id" FROM "FeedItems" fi
                                     INNER JOIN "MediaSubscriptions" ms ON fi."SubscriptionId" = ms."Id"
                                     WHERE ms."IsOneShot" = 1);
                                 """);
            // 2. 择优记录随一次性条目清除（组键释放，下次刷新可重选）。
            migrationBuilder.Sql("""
                                 DELETE FROM "CandidateSelections"
                                 WHERE "WinnerFeedItemId" IN (
                                     SELECT fi."Id" FROM "FeedItems" fi
                                     INNER JOIN "MediaSubscriptions" ms ON fi."SubscriptionId" = ms."Id"
                                     WHERE ms."IsOneShot" = 1);
                                 """);
            // 3. 刷新工作与条目，最后删除订阅本身。
            migrationBuilder.Sql("""
                                 DELETE FROM "RefreshWorkItems"
                                 WHERE "SubscriptionId" IN (SELECT "Id" FROM "MediaSubscriptions" WHERE "IsOneShot" = 1);
                                 """);
            migrationBuilder.Sql("""
                                 DELETE FROM "FeedItems"
                                 WHERE "SubscriptionId" IN (SELECT "Id" FROM "MediaSubscriptions" WHERE "IsOneShot" = 1);
                                 """);
            migrationBuilder.Sql("""
                                 DELETE FROM "MediaSubscriptions" WHERE "IsOneShot" = 1;
                                 """);
            migrationBuilder.DropColumn(
                name: "IsOneShot",
                table: "MediaSubscriptions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 删除的一次性订阅与条目数据无法恢复，只恢复列本身。
            migrationBuilder.AddColumn<bool>(
                name: "IsOneShot",
                table: "MediaSubscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
