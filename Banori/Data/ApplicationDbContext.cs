using Banori.Features.Downloads;
using Banori.Features.Subscriptions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Banori.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ApplicationSetting>   ApplicationSettings    => Set<ApplicationSetting>();
    public DbSet<MediaSubscription>    MediaSubscriptions     => Set<MediaSubscription>();
    public DbSet<RefreshWorkItem>      RefreshWorkItems       => Set<RefreshWorkItem>();
    public DbSet<FeedItem>             FeedItems              => Set<FeedItem>();
    public DbSet<MediaSubject>         MediaSubjects          => Set<MediaSubject>();
    public DbSet<SubjectTitleMapping>  SubjectTitleMappings   => Set<SubjectTitleMapping>();
    public DbSet<DownloadTask>         DownloadTasks          => Set<DownloadTask>();
    public DbSet<DownloadTaskFile>     DownloadTaskFiles      => Set<DownloadTaskFile>();
    public DbSet<EpisodeVersion>       EpisodeVersions        => Set<EpisodeVersion>();
    public DbSet<CandidateSelection>   CandidateSelections    => Set<CandidateSelection>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<ProxyServer>          ProxyServers           => Set<ProxyServer>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationSetting>(entity =>
        {
            entity.ToTable("ApplicationSettings");
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(ApplicationSetting.KeyMaxLength);
            entity.Property(setting => setting.Value).IsRequired();
            entity.Property(setting => setting.UpdatedAtUtc).IsRequired();
        });

        builder.Entity<MediaSubscription>(entity =>
        {
            entity.ToTable("MediaSubscriptions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(100).IsRequired();
            entity.Property(item => item.FeedUrlProtected).IsRequired();
            entity.Property(item => item.LastError).HasMaxLength(1000);
            entity.HasOne(item => item.Proxy).WithMany()
                  .HasForeignKey(item => item.ProxyId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ProxyServer>(entity =>
        {
            entity.ToTable("ProxyServers");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(50).IsRequired();
            entity.Property(item => item.Protocol).HasConversion<string>().HasMaxLength(16);
            entity.Property(item => item.Host).HasMaxLength(255).IsRequired();
            entity.Property(item => item.Username).HasMaxLength(100);
            entity.Property(item => item.PasswordProtected).HasMaxLength(1000);
            entity.HasIndex(item => item.Name).IsUnique();
        });

        builder.Entity<RefreshWorkItem>(entity =>
        {
            entity.ToTable("RefreshWorkItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Error).HasMaxLength(1000);
            entity.HasIndex(item => new { item.SubscriptionId, item.Status });
            entity.HasOne(item => item.Subscription).WithMany(item => item.RefreshWorkItems)
                  .HasForeignKey(item => item.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MediaSubject>(entity =>
        {
            entity.ToTable("MediaSubjects");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(500).IsRequired();
            entity.Property(item => item.NameCn).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Platform).HasMaxLength(100).IsRequired();
            entity.Property(item => item.AirDate).HasMaxLength(20);
            entity.Property(item => item.ImageUrl).HasMaxLength(2000);
        });

        builder.Entity<SubjectTitleMapping>(entity =>
        {
            entity.ToTable("SubjectTitleMappings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.NormalizedTitle).HasMaxLength(500).IsRequired();
            entity.HasIndex(item => item.NormalizedTitle).IsUnique();
            entity.HasOne(item => item.Subject).WithMany(item => item.TitleMappings)
                  .HasForeignKey(item => item.SubjectId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<FeedItem>(entity =>
        {
            entity.ToTable("FeedItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.StableId).HasMaxLength(512).IsRequired();
            entity.Property(item => item.SourceItemId).HasMaxLength(1000);
            entity.Property(item => item.Title).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.Link).HasMaxLength(2000);
            entity.Property(item => item.TorrentUrl).HasMaxLength(2000);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.DecisionStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ParsedTitle).HasMaxLength(500);
            entity.Property(item => item.MediaType).HasMaxLength(50);
            entity.Property(item => item.Group).HasMaxLength(200);
            entity.Property(item => item.GroupType).HasMaxLength(50);
            entity.Property(item => item.Language).HasMaxLength(50);
            entity.Property(item => item.SubtitleType).HasMaxLength(50);
            entity.Property(item => item.Resolution).HasMaxLength(50);
            entity.Property(item => item.Source).HasMaxLength(50);
            entity.Property(item => item.WebSource).HasMaxLength(200);
            entity.Property(item => item.VideoCodec).HasMaxLength(100);
            entity.Property(item => item.AudioCodec).HasMaxLength(100);
            entity.Property(item => item.Preference).HasMaxLength(100);
            entity.Property(item => item.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.NamingPreview).HasMaxLength(1000);
            entity.Property(item => item.NamingIngredients);
            // 显式默认值：存量行升级时必须得到合法枚举值，空字符串会破坏字符串转换读取。
            entity.Property(item => item.SelectionOutcome).HasConversion<string>().HasMaxLength(32)
                  .HasDefaultValue(SelectionOutcome.None);
            entity.HasIndex(item => new { item.SubscriptionId, item.StableId }).IsUnique();
            entity.HasIndex(item => new { item.SubjectId, item.Episode, item.Status });
            entity.HasOne(item => item.Subscription).WithMany(item => item.FeedItems)
                  .HasForeignKey(item => item.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Subject).WithMany(item => item.FeedItems)
                  .HasForeignKey(item => item.SubjectId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<DownloadTask>(entity =>
        {
            entity.ToTable("DownloadTasks");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.InfoHash).HasMaxLength(40).IsRequired();
            entity.Property(item => item.TorrentUrl).HasMaxLength(2000);
            entity.Property(item => item.SavePath).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.DownloadRootPath).HasMaxLength(1000);
            entity.Property(item => item.MediaDirectory).HasMaxLength(1000);
            entity.Property(item => item.SeedDirectory).HasMaxLength(1000);
            entity.Property(item => item.PendingSavePath).HasMaxLength(1000);
            entity.Property(item => item.SiteSource).HasMaxLength(50);
            entity.Property(item => item.NamingSnapshot).HasMaxLength(200).IsRequired();
            entity.Property(item => item.NamingIngredients);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            // 显式默认值：存量行升级时必须得到合法枚举值，空字符串会破坏字符串转换读取。
            entity.Property(item => item.PreparationStage).HasConversion<string>().HasMaxLength(32)
                  .HasDefaultValue(DownloadPreparationStage.None);
            entity.Property(item => item.FailedStep).HasMaxLength(50);
            entity.Property(item => item.Error).HasMaxLength(1000);
            entity.HasIndex(item => item.FeedItemId);
            entity.HasIndex(item => new { item.SubjectId, item.Episode, item.Status });
            // 同一资源在活动状态下只允许一个任务；终态任务可并存（如被替换后的历史记录）。
            // MySQL 不支持带 WHERE 的部分索引，统一用存储型计算列表达“是否活动”：
            // 活动行取 1、终态行取 NULL，唯一索引对 NULL 不去重，与 SQLite 部分索引语义完全一致。
            // 反引号标识符 SQLite 同样接受，表达式字符串可保持一致。
            entity.Property<int?>("ActiveFlag")
                  .HasComputedColumnSql(
                                        "CASE WHEN `Status` IN ('PendingSubmit', 'Submitting', 'Downloading', 'Paused') " +
                                        "THEN 1 ELSE NULL END", true);
            entity.HasIndex("InfoHash", "ActiveFlag").IsUnique();
            entity.HasOne(item => item.FeedItem).WithMany()
                  .HasForeignKey(item => item.FeedItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Subject).WithMany()
                  .HasForeignKey(item => item.SubjectId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<DownloadTaskFile>(entity =>
        {
            entity.ToTable("DownloadTaskFiles");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.OriginalRelativePath).IsRequired();
            entity.Property(item => item.CurrentRelativePath).IsRequired();
            entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.EpisodeNumberOverride).HasPrecision(18, 4);
            entity.Property(item => item.EpisodeEndOverride).HasPrecision(18, 4);
            entity.Property(item => item.ClassificationReason).HasMaxLength(1000).IsRequired();
            // 存量行升级时默认承担下载：跳过必须由择优计划显式声明。
            entity.Property(item => item.IsSelectedForDownload).HasDefaultValue(true);
            entity.Property(item => item.SelectionReason).HasMaxLength(1000);
            entity.HasIndex(item => new { item.DownloadTaskId, item.FileIndex }).IsUnique();
            entity.HasOne(item => item.DownloadTask).WithMany(item => item.Files)
                  .HasForeignKey(item => item.DownloadTaskId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EpisodeVersion>(entity =>
        {
            entity.ToTable("EpisodeVersions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.MediaType).HasMaxLength(50).IsRequired();
            entity.Property(item => item.Episode).HasPrecision(18, 4);
            // 文件引用不设外键：承接方任务删除时由清理逻辑回退引用，而不是级联清除登记。
            entity.HasIndex(item => new { item.SubjectId, item.Episode }).IsUnique();
        });

        builder.Entity<CandidateSelection>(entity =>
        {
            entity.ToTable("CandidateSelections");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.MediaType).HasMaxLength(50).IsRequired();
            entity.Property(item => item.RankingBasis).HasMaxLength(500).IsRequired();
            entity.Property(item => item.NamingSnapshot).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.IntentFailedReason).HasMaxLength(1000);
            // 每个同集分组只保留一条当前优胜选择。
            entity.HasIndex(item => new { item.SubjectId, item.MediaType, item.Episode }).IsUnique();
            entity.HasIndex(item => item.IntentTaskId);
            entity.HasOne(item => item.Subject).WithMany()
                  .HasForeignKey(item => item.SubjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.WinnerFeedItem).WithMany()
                  .HasForeignKey(item => item.WinnerFeedItemId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NotificationDelivery>(entity =>
        {
            entity.ToTable("NotificationDeliveries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Channel).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.EventType).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Title).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Message).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LastError).HasMaxLength(1000);
            entity.HasIndex(item => new { item.Status, item.NextAttemptAtUtc });
        });
    }
}
