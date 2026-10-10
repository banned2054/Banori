#nullable disable

using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MediaManager.Data.Migrations.MySql;

/// <inheritdoc />
public partial class InitialMySqlCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "ApplicationSettings",
                                     table => new
                                     {
                                         Key = table.Column<string>("varchar(200)", maxLength : 200, nullable : false)
                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         Value = table.Column<string>("longtext", nullable : false)
                                                      .Annotation("MySql:CharSet", "utf8mb4"),
                                         IsProtected  = table.Column<bool>("tinyint(1)", nullable : false),
                                         UpdatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false)
                                     },
                                     constraints : table => { table.PrimaryKey("PK_ApplicationSettings", x => x.Key); })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "AspNetRoles",
                                     table => new
                                     {
                                         Id = table.Column<string>("varchar(255)", nullable : false)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         Name = table.Column<string>("varchar(256)", maxLength : 256, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         NormalizedName = table
                                                         .Column<string>("varchar(256)", maxLength : 256,
                                                                         nullable : true)
                                                         .Annotation("MySql:CharSet", "utf8mb4"),
                                         ConcurrencyStamp = table.Column<string>("longtext", nullable : true)
                                                                 .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table => { table.PrimaryKey("PK_AspNetRoles", x => x.Id); })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "AspNetUsers",
                                     table => new
                                     {
                                         Id = table.Column<string>("varchar(255)", nullable : false)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         UserName = table
                                                   .Column<string>("varchar(256)", maxLength : 256, nullable : true)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         NormalizedUserName = table
                                                             .Column<string>("varchar(256)", maxLength : 256,
                                                                             nullable : true)
                                                             .Annotation("MySql:CharSet", "utf8mb4"),
                                         Email = table.Column<string>("varchar(256)", maxLength : 256, nullable : true)
                                                      .Annotation("MySql:CharSet", "utf8mb4"),
                                         NormalizedEmail = table
                                                          .Column<string>("varchar(256)", maxLength : 256,
                                                                          nullable : true)
                                                          .Annotation("MySql:CharSet", "utf8mb4"),
                                         EmailConfirmed = table.Column<bool>("tinyint(1)", nullable : false),
                                         PasswordHash = table.Column<string>("longtext", nullable : true)
                                                             .Annotation("MySql:CharSet", "utf8mb4"),
                                         SecurityStamp = table.Column<string>("longtext", nullable : true)
                                                              .Annotation("MySql:CharSet", "utf8mb4"),
                                         ConcurrencyStamp = table.Column<string>("longtext", nullable : true)
                                                                 .Annotation("MySql:CharSet", "utf8mb4"),
                                         PhoneNumber = table
                                                      .Column<string>("varchar(256)", maxLength : 256, nullable : true)
                                                      .Annotation("MySql:CharSet", "utf8mb4"),
                                         PhoneNumberConfirmed = table.Column<bool>("tinyint(1)", nullable : false),
                                         TwoFactorEnabled = table.Column<bool>("tinyint(1)", nullable : false),
                                         LockoutEnd = table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         LockoutEnabled = table.Column<bool>("tinyint(1)", nullable : false),
                                         AccessFailedCount = table.Column<int>("int", nullable : false)
                                     },
                                     constraints : table => { table.PrimaryKey("PK_AspNetUsers", x => x.Id); })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "MediaSubjects",
                                     table => new
                                     {
                                         Id = table.Column<int>("int", nullable : false)
                                                   .Annotation("MySql:ValueGenerationStrategy",
                                                               MySqlValueGenerationStrategy.IdentityColumn),
                                         Name = table.Column<string>("varchar(500)", maxLength : 500, nullable : false)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         NameCn = table
                                                 .Column<string>("varchar(500)", maxLength : 500, nullable : false)
                                                 .Annotation("MySql:CharSet", "utf8mb4"),
                                         Platform = table
                                                   .Column<string>("varchar(100)", maxLength : 100, nullable : false)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         AirDate = table.Column<string>("varchar(20)", maxLength : 20, nullable : true)
                                                        .Annotation("MySql:CharSet", "utf8mb4"),
                                         ImageUrl = table
                                                   .Column<string>("varchar(2000)", maxLength : 2000, nullable : true)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         CachedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false)
                                     },
                                     constraints : table => { table.PrimaryKey("PK_MediaSubjects", x => x.Id); })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "NotificationDeliveries",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         Channel = table.Column<string>("varchar(32)", maxLength : 32, nullable : false)
                                                        .Annotation("MySql:CharSet", "utf8mb4"),
                                         EventType = table
                                                    .Column<string>("varchar(32)", maxLength : 32, nullable : false)
                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         Title = table.Column<string>("varchar(200)", maxLength : 200, nullable : false)
                                                      .Annotation("MySql:CharSet", "utf8mb4"),
                                         Message = table
                                                  .Column<string>("varchar(2000)", maxLength : 2000, nullable : false)
                                                  .Annotation("MySql:CharSet", "utf8mb4"),
                                         Status = table.Column<string>("varchar(32)", maxLength : 32, nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         AttemptCount = table.Column<int>("int", nullable : false),
                                         NextAttemptAtUtc =
                                             table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         LastError = table
                                                    .Column<string>("varchar(1000)", maxLength : 1000, nullable : true)
                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         CreatedAtUtc   = table.Column<DateTimeOffset>("datetime(6)", nullable : false),
                                         DeliveredAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : true)
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "ProxyServers",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         Name = table.Column<string>("varchar(50)", maxLength : 50, nullable : false)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         Protocol = table
                                                   .Column<string>("varchar(16)", maxLength : 16, nullable : false)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         Host = table.Column<string>("varchar(255)", maxLength : 255, nullable : false)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         Port = table.Column<int>("int", nullable : false),
                                         Username = table
                                                   .Column<string>("varchar(100)", maxLength : 100, nullable : true)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         PasswordProtected = table
                                                            .Column<string>("varchar(1000)", maxLength : 1000,
                                                                            nullable : true)
                                                            .Annotation("MySql:CharSet", "utf8mb4"),
                                         CreatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false),
                                         UpdatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false)
                                     },
                                     constraints : table => { table.PrimaryKey("PK_ProxyServers", x => x.Id); })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "AspNetRoleClaims",
                                     table => new
                                     {
                                         Id = table.Column<int>("int", nullable : false)
                                                   .Annotation("MySql:ValueGenerationStrategy",
                                                               MySqlValueGenerationStrategy.IdentityColumn),
                                         RoleId = table.Column<string>("varchar(255)", nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         ClaimType = table.Column<string>("longtext", nullable : true)
                                                          .Annotation("MySql:CharSet", "utf8mb4"),
                                         ClaimValue = table.Column<string>("longtext", nullable : true)
                                                           .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                                                          x => x.RoleId,
                                                          "AspNetRoles",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "AspNetUserClaims",
                                     table => new
                                     {
                                         Id = table.Column<int>("int", nullable : false)
                                                   .Annotation("MySql:ValueGenerationStrategy",
                                                               MySqlValueGenerationStrategy.IdentityColumn),
                                         UserId = table.Column<string>("varchar(255)", nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         ClaimType = table.Column<string>("longtext", nullable : true)
                                                          .Annotation("MySql:CharSet", "utf8mb4"),
                                         ClaimValue = table.Column<string>("longtext", nullable : true)
                                                           .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_AspNetUserClaims_AspNetUsers_UserId",
                                                          x => x.UserId,
                                                          "AspNetUsers",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "AspNetUserLogins",
                                     table => new
                                     {
                                         LoginProvider = table
                                                        .Column<string>("varchar(128)", maxLength : 128,
                                                                        nullable : false)
                                                        .Annotation("MySql:CharSet", "utf8mb4"),
                                         ProviderKey = table
                                                      .Column<string>("varchar(128)", maxLength : 128, nullable : false)
                                                      .Annotation("MySql:CharSet", "utf8mb4"),
                                         ProviderDisplayName = table.Column<string>("longtext", nullable : true)
                                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         UserId = table.Column<string>("varchar(255)", nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_AspNetUserLogins",
                                                          x => new { x.LoginProvider, x.ProviderKey });
                                         table.ForeignKey(
                                                          "FK_AspNetUserLogins_AspNetUsers_UserId",
                                                          x => x.UserId,
                                                          "AspNetUsers",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "AspNetUserPasskeys",
                                     table => new
                                     {
                                         CredentialId =
                                             table.Column<byte[]>("varbinary(1024)", maxLength : 1024,
                                                                  nullable : false),
                                         UserId = table.Column<string>("varchar(255)", nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         Data = table.Column<string>("json", nullable : false)
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_AspNetUserPasskeys", x => x.CredentialId);
                                         table.ForeignKey(
                                                          "FK_AspNetUserPasskeys_AspNetUsers_UserId",
                                                          x => x.UserId,
                                                          "AspNetUsers",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "AspNetUserRoles",
                                     table => new
                                     {
                                         UserId = table.Column<string>("varchar(255)", nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         RoleId = table.Column<string>("varchar(255)", nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                                         table.ForeignKey(
                                                          "FK_AspNetUserRoles_AspNetRoles_RoleId",
                                                          x => x.RoleId,
                                                          "AspNetRoles",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                         table.ForeignKey(
                                                          "FK_AspNetUserRoles_AspNetUsers_UserId",
                                                          x => x.UserId,
                                                          "AspNetUsers",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "AspNetUserTokens",
                                     table => new
                                     {
                                         UserId = table.Column<string>("varchar(255)", nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         LoginProvider = table
                                                        .Column<string>("varchar(128)", maxLength : 128,
                                                                        nullable : false)
                                                        .Annotation("MySql:CharSet", "utf8mb4"),
                                         Name = table.Column<string>("varchar(128)", maxLength : 128, nullable : false)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         Value = table.Column<string>("longtext", nullable : true)
                                                      .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_AspNetUserTokens",
                                                          x => new { x.UserId, x.LoginProvider, x.Name });
                                         table.ForeignKey(
                                                          "FK_AspNetUserTokens_AspNetUsers_UserId",
                                                          x => x.UserId,
                                                          "AspNetUsers",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "SubjectTitleMappings",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         NormalizedTitle = table
                                                          .Column<string>("varchar(500)", maxLength : 500,
                                                                          nullable : false)
                                                          .Annotation("MySql:CharSet", "utf8mb4"),
                                         SubjectId    = table.Column<int>("int", nullable : false),
                                         UpdatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false)
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_SubjectTitleMappings", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_SubjectTitleMappings_MediaSubjects_SubjectId",
                                                          x => x.SubjectId,
                                                          "MediaSubjects",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "MediaSubscriptions",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         Name = table.Column<string>("varchar(100)", maxLength : 100, nullable : false)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         FeedUrlProtected = table.Column<string>("longtext", nullable : false)
                                                                 .Annotation("MySql:CharSet", "utf8mb4"),
                                         IsEnabled              = table.Column<bool>("tinyint(1)", nullable : false),
                                         RefreshIntervalMinutes = table.Column<int>("int", nullable : false),
                                         FixedSubjectId         = table.Column<int>("int", nullable : true),
                                         ProxyId = table.Column<Guid>("char(36)", nullable : true,
                                                                      collation : "ascii_general_ci"),
                                         CreatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false),
                                         UpdatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false),
                                         LastRefreshStartedAtUtc =
                                             table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         LastRefreshCompletedAtUtc =
                                             table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         NextRefreshAtUtc =
                                             table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         LastError = table
                                                    .Column<string>("varchar(1000)", maxLength : 1000, nullable : true)
                                                    .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_MediaSubscriptions", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_MediaSubscriptions_ProxyServers_ProxyId",
                                                          x => x.ProxyId,
                                                          "ProxyServers",
                                                          "Id",
                                                          onDelete : ReferentialAction.SetNull);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "FeedItems",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         SubscriptionId =
                                             table.Column<Guid>("char(36)", nullable : false,
                                                                collation : "ascii_general_ci"),
                                         StableId = table
                                                   .Column<string>("varchar(512)", maxLength : 512, nullable : false)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         SourceItemId = table
                                                       .Column<string>("varchar(1000)", maxLength : 1000,
                                                                       nullable : true)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         Title = table
                                                .Column<string>("varchar(2000)", maxLength : 2000, nullable : false)
                                                .Annotation("MySql:CharSet", "utf8mb4"),
                                         Link = table.Column<string>("varchar(2000)", maxLength : 2000, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         TorrentUrl = table
                                                     .Column<string>("varchar(2000)", maxLength : 2000, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         PublishedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         FirstSeenAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false),
                                         EvaluatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         Status = table.Column<string>("varchar(32)", maxLength : 32, nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         ParsedTitle = table
                                                      .Column<string>("varchar(500)", maxLength : 500, nullable : true)
                                                      .Annotation("MySql:CharSet", "utf8mb4"),
                                         MediaType = table
                                                    .Column<string>("varchar(50)", maxLength : 50, nullable : true)
                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         Episode      = table.Column<decimal>("decimal(65,30)", nullable : true),
                                         StartEpisode = table.Column<int>("int", nullable : true),
                                         EndEpisode   = table.Column<int>("int", nullable : true),
                                         Version      = table.Column<int>("int", nullable : false),
                                         Group = table.Column<string>("varchar(200)", maxLength : 200, nullable : true)
                                                      .Annotation("MySql:CharSet", "utf8mb4"),
                                         GroupType = table
                                                    .Column<string>("varchar(50)", maxLength : 50, nullable : true)
                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         Language = table.Column<string>("varchar(50)", maxLength : 50, nullable : true)
                                                         .Annotation("MySql:CharSet", "utf8mb4"),
                                         SubtitleType = table
                                                       .Column<string>("varchar(50)", maxLength : 50, nullable : true)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         Resolution = table
                                                     .Column<string>("varchar(50)", maxLength : 50, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         Source = table.Column<string>("varchar(50)", maxLength : 50, nullable : true)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         WebSource = table
                                                    .Column<string>("varchar(200)", maxLength : 200, nullable : true)
                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         VideoCodec = table
                                                     .Column<string>("varchar(100)", maxLength : 100, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         AudioCodec = table
                                                     .Column<string>("varchar(100)", maxLength : 100, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         ColorBitDepth = table.Column<int>("int", nullable : true),
                                         SubjectId     = table.Column<int>("int", nullable : true),
                                         DecisionStatus = table
                                                         .Column<string>("varchar(32)", maxLength : 32, nullable : true)
                                                         .Annotation("MySql:CharSet", "utf8mb4"),
                                         Preference = table
                                                     .Column<string>("varchar(100)", maxLength : 100, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         Reason = table
                                                 .Column<string>("varchar(1000)", maxLength : 1000, nullable : false)
                                                 .Annotation("MySql:CharSet", "utf8mb4"),
                                         NamingPreview = table
                                                        .Column<string>("varchar(1000)", maxLength : 1000,
                                                                        nullable : true)
                                                        .Annotation("MySql:CharSet", "utf8mb4"),
                                         SelectionOutcome = table
                                                           .Column<string>("varchar(32)", maxLength : 32,
                                                                           nullable : false, defaultValue : "None")
                                                           .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_FeedItems", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_FeedItems_MediaSubjects_SubjectId",
                                                          x => x.SubjectId,
                                                          "MediaSubjects",
                                                          "Id",
                                                          onDelete : ReferentialAction.SetNull);
                                         table.ForeignKey(
                                                          "FK_FeedItems_MediaSubscriptions_SubscriptionId",
                                                          x => x.SubscriptionId,
                                                          "MediaSubscriptions",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "RefreshWorkItems",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         SubscriptionId =
                                             table.Column<Guid>("char(36)", nullable : false,
                                                                collation : "ascii_general_ci"),
                                         Status = table.Column<string>("varchar(32)", maxLength : 32, nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         AttemptCount   = table.Column<int>("int", nullable : false),
                                         RequestedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false),
                                         StartedAtUtc   = table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         CompletedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         Error = table
                                                .Column<string>("varchar(1000)", maxLength : 1000, nullable : true)
                                                .Annotation("MySql:CharSet", "utf8mb4")
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_RefreshWorkItems", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_RefreshWorkItems_MediaSubscriptions_SubscriptionId",
                                                          x => x.SubscriptionId,
                                                          "MediaSubscriptions",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "CandidateSelections",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         SubjectId = table.Column<int>("int", nullable : false),
                                         MediaType = table
                                                    .Column<string>("varchar(50)", maxLength : 50, nullable : false)
                                                    .Annotation("MySql:CharSet", "utf8mb4"),
                                         Episode = table.Column<decimal>("decimal(65,30)", nullable : false),
                                         WinnerFeedItemId =
                                             table.Column<Guid>("char(36)", nullable : false,
                                                                collation : "ascii_general_ci"),
                                         RuleVersion = table.Column<int>("int", nullable : false),
                                         RankingBasis = table
                                                       .Column<string>("varchar(500)", maxLength : 500,
                                                                       nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         NamingSnapshot = table
                                                         .Column<string>("varchar(1000)", maxLength : 1000,
                                                                         nullable : false)
                                                         .Annotation("MySql:CharSet", "utf8mb4"),
                                         IntentTaskId =
                                             table.Column<Guid>("char(36)", nullable : true,
                                                                collation : "ascii_general_ci"),
                                         IntentFailedReason = table
                                                             .Column<string>("varchar(1000)", maxLength : 1000,
                                                                             nullable : true)
                                                             .Annotation("MySql:CharSet", "utf8mb4"),
                                         SelectedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false)
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_CandidateSelections", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_CandidateSelections_FeedItems_WinnerFeedItemId",
                                                          x => x.WinnerFeedItemId,
                                                          "FeedItems",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                         table.ForeignKey(
                                                          "FK_CandidateSelections_MediaSubjects_SubjectId",
                                                          x => x.SubjectId,
                                                          "MediaSubjects",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
                                     "DownloadTasks",
                                     table => new
                                     {
                                         Id = table.Column<Guid>("char(36)", nullable : false,
                                                                 collation : "ascii_general_ci"),
                                         FeedItemId =
                                             table.Column<Guid>("char(36)", nullable : true,
                                                                collation : "ascii_general_ci"),
                                         SubjectId = table.Column<int>("int", nullable : true),
                                         InfoHash = table
                                                   .Column<string>("varchar(40)", maxLength : 40, nullable : false)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         TorrentFile = table.Column<byte[]>("longblob", nullable : false),
                                         TorrentUrl = table
                                                     .Column<string>("varchar(2000)", maxLength : 2000, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         SavePath = table
                                                   .Column<string>("varchar(1000)", maxLength : 1000, nullable : false)
                                                   .Annotation("MySql:CharSet", "utf8mb4"),
                                         NamingSnapshot = table
                                                         .Column<string>("varchar(200)", maxLength : 200,
                                                                         nullable : false)
                                                         .Annotation("MySql:CharSet", "utf8mb4"),
                                         Episode = table.Column<decimal>("decimal(65,30)", nullable : true),
                                         Version = table.Column<int>("int", nullable : false),
                                         Status = table.Column<string>("varchar(32)", maxLength : 32, nullable : false)
                                                       .Annotation("MySql:CharSet", "utf8mb4"),
                                         IsUserPaused = table.Column<bool>("tinyint(1)", nullable : false),
                                         AttemptCount = table.Column<int>("int", nullable : false),
                                         Progress     = table.Column<double>("double", nullable : true),
                                         TotalSize    = table.Column<long>("bigint", nullable : true),
                                         ReplacesTaskId =
                                             table.Column<Guid>("char(36)", nullable : true,
                                                                collation : "ascii_general_ci"),
                                         ReplacedByTaskId =
                                             table.Column<Guid>("char(36)", nullable : true,
                                                                collation : "ascii_general_ci"),
                                         CreatedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : false),
                                         SubmittedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         CompletedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         LastSyncedAtUtc = table.Column<DateTimeOffset>("datetime(6)", nullable : true),
                                         FailedStep = table
                                                     .Column<string>("varchar(50)", maxLength : 50, nullable : true)
                                                     .Annotation("MySql:CharSet", "utf8mb4"),
                                         Error = table
                                                .Column<string>("varchar(1000)", maxLength : 1000, nullable : true)
                                                .Annotation("MySql:CharSet", "utf8mb4"),
                                         ActiveFlag = table.Column<int>("int", nullable : true,
                                                                        computedColumnSql :
                                                                        "CASE WHEN `Status` IN ('PendingSubmit', 'Submitting', 'Downloading', 'Paused') THEN 1 ELSE NULL END",
                                                                        stored : true)
                                     },
                                     constraints : table =>
                                     {
                                         table.PrimaryKey("PK_DownloadTasks", x => x.Id);
                                         table.ForeignKey(
                                                          "FK_DownloadTasks_FeedItems_FeedItemId",
                                                          x => x.FeedItemId,
                                                          "FeedItems",
                                                          "Id",
                                                          onDelete : ReferentialAction.Cascade);
                                         table.ForeignKey(
                                                          "FK_DownloadTasks_MediaSubjects_SubjectId",
                                                          x => x.SubjectId,
                                                          "MediaSubjects",
                                                          "Id",
                                                          onDelete : ReferentialAction.SetNull);
                                     })
                        .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
                                     "IX_AspNetRoleClaims_RoleId",
                                     "AspNetRoleClaims",
                                     "RoleId");

        migrationBuilder.CreateIndex(
                                     "RoleNameIndex",
                                     "AspNetRoles",
                                     "NormalizedName",
                                     unique : true);

        migrationBuilder.CreateIndex(
                                     "IX_AspNetUserClaims_UserId",
                                     "AspNetUserClaims",
                                     "UserId");

        migrationBuilder.CreateIndex(
                                     "IX_AspNetUserLogins_UserId",
                                     "AspNetUserLogins",
                                     "UserId");

        migrationBuilder.CreateIndex(
                                     "IX_AspNetUserPasskeys_UserId",
                                     "AspNetUserPasskeys",
                                     "UserId");

        migrationBuilder.CreateIndex(
                                     "IX_AspNetUserRoles_RoleId",
                                     "AspNetUserRoles",
                                     "RoleId");

        migrationBuilder.CreateIndex(
                                     "EmailIndex",
                                     "AspNetUsers",
                                     "NormalizedEmail");

        migrationBuilder.CreateIndex(
                                     "UserNameIndex",
                                     "AspNetUsers",
                                     "NormalizedUserName",
                                     unique : true);

        migrationBuilder.CreateIndex(
                                     "IX_CandidateSelections_IntentTaskId",
                                     "CandidateSelections",
                                     "IntentTaskId");

        migrationBuilder.CreateIndex(
                                     "IX_CandidateSelections_SubjectId_MediaType_Episode",
                                     "CandidateSelections",
                                     new[] { "SubjectId", "MediaType", "Episode" },
                                     unique : true);

        migrationBuilder.CreateIndex(
                                     "IX_CandidateSelections_WinnerFeedItemId",
                                     "CandidateSelections",
                                     "WinnerFeedItemId");

        migrationBuilder.CreateIndex(
                                     "IX_DownloadTasks_FeedItemId",
                                     "DownloadTasks",
                                     "FeedItemId");

        migrationBuilder.CreateIndex(
                                     "IX_DownloadTasks_InfoHash_ActiveFlag",
                                     "DownloadTasks",
                                     new[] { "InfoHash", "ActiveFlag" },
                                     unique : true);

        migrationBuilder.CreateIndex(
                                     "IX_DownloadTasks_SubjectId_Episode_Status",
                                     "DownloadTasks",
                                     new[] { "SubjectId", "Episode", "Status" });

        migrationBuilder.CreateIndex(
                                     "IX_FeedItems_SubjectId_Episode_Status",
                                     "FeedItems",
                                     new[] { "SubjectId", "Episode", "Status" });

        migrationBuilder.CreateIndex(
                                     "IX_FeedItems_SubscriptionId_StableId",
                                     "FeedItems",
                                     new[] { "SubscriptionId", "StableId" },
                                     unique : true);

        migrationBuilder.CreateIndex(
                                     "IX_MediaSubscriptions_ProxyId",
                                     "MediaSubscriptions",
                                     "ProxyId");

        migrationBuilder.CreateIndex(
                                     "IX_NotificationDeliveries_Status_NextAttemptAtUtc",
                                     "NotificationDeliveries",
                                     new[] { "Status", "NextAttemptAtUtc" });

        migrationBuilder.CreateIndex(
                                     "IX_ProxyServers_Name",
                                     "ProxyServers",
                                     "Name",
                                     unique : true);

        migrationBuilder.CreateIndex(
                                     "IX_RefreshWorkItems_SubscriptionId_Status",
                                     "RefreshWorkItems",
                                     new[] { "SubscriptionId", "Status" });

        migrationBuilder.CreateIndex(
                                     "IX_SubjectTitleMappings_NormalizedTitle",
                                     "SubjectTitleMappings",
                                     "NormalizedTitle",
                                     unique : true);

        migrationBuilder.CreateIndex(
                                     "IX_SubjectTitleMappings_SubjectId",
                                     "SubjectTitleMappings",
                                     "SubjectId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
                                   "ApplicationSettings");

        migrationBuilder.DropTable(
                                   "AspNetRoleClaims");

        migrationBuilder.DropTable(
                                   "AspNetUserClaims");

        migrationBuilder.DropTable(
                                   "AspNetUserLogins");

        migrationBuilder.DropTable(
                                   "AspNetUserPasskeys");

        migrationBuilder.DropTable(
                                   "AspNetUserRoles");

        migrationBuilder.DropTable(
                                   "AspNetUserTokens");

        migrationBuilder.DropTable(
                                   "CandidateSelections");

        migrationBuilder.DropTable(
                                   "DownloadTasks");

        migrationBuilder.DropTable(
                                   "NotificationDeliveries");

        migrationBuilder.DropTable(
                                   "RefreshWorkItems");

        migrationBuilder.DropTable(
                                   "SubjectTitleMappings");

        migrationBuilder.DropTable(
                                   "AspNetRoles");

        migrationBuilder.DropTable(
                                   "AspNetUsers");

        migrationBuilder.DropTable(
                                   "FeedItems");

        migrationBuilder.DropTable(
                                   "MediaSubjects");

        migrationBuilder.DropTable(
                                   "MediaSubscriptions");

        migrationBuilder.DropTable(
                                   "ProxyServers");
    }
}
