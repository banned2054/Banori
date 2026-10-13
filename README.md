# Banori

Banori 是面向个人或家庭媒体库的动漫 RSS 管理面板。当前已完成 P0、P1、P2、P2R 和 P3：可以管理 Mikan 订阅、持久化刷新任务、去重保存条目、关联 Bangumi Subject，展示筛选理由与命名预览，评估优胜的条目自动登记为下载任务，由后台提交 qBittorrent、按 info-hash 对账直至完成改名。

当前工作见 [结构整理计划](plan.md)；已实现阶段、历史设计与验收边界见 [历史计划归档](past-plan.md)。

## 项目结构

```text
Banori.Core/        标题归一化、筛选、集数修正、命名预览、info-hash 提取、路径净化等纯规则，可独立测试
Banori/
  Components/Pages/       订阅、下载任务、设置页面及页面 ViewModel
  Features/               业务服务：订阅管理、Mikan 刷新、条目评估、下载登记（DownloadIntakeService）、
                          下载执行与生命周期（DownloadService）、任务列表查询（DownloadTaskQueries）、通知、设置
  Data/                   EF Core 实体与双 provider 迁移（SQLite 默认 / MySQL）
  Integrations/           Mikan、Bangumi、qBittorrent 客户端与种子下载
  Workers/                周期调度 Worker（订阅刷新、下载对账、通知投递）
Banori.Tests/       规则单元测试、SQLite 行为测试
```

刷新链路：订阅页 / 到期调度 → `SubscriptionService` 入队 → `MikanRefreshService` 执行（读 RSS、去重入库、评估遗留条目）→ `FeedItemEvaluator`（AniParser 解析、Subject 关联、Core 规则决策、命名预览与命名要素）→ 保存结果与下次刷新时间。

登记：优胜条目自动登记 / RSS 链接下载 / 本地上传种子三个入口统一走 `DownloadIntakeService`（下载种子、提取 info-hash、Bangumi 绑定门禁、同集统一裁决、登记 `DownloadTask`；上传大小上限在登记入口校验）。登记事务只落任务与替换状态，不登录下载器；被顶替未完成任务的远端清理由 `DownloadReconcileWorker` 的对账在提交后继续，失败或重启后可恢复。任务列表页的只读投影由 `DownloadTaskQueries` 提供。

执行：`DownloadReconcileWorker` 周期对账（`DownloadService`）→ 提交 qBittorrent（qB 种子名称与种子内文件名称分别使用独立模板，模板及 AniParser/Bangumi 元数据一并快照落库）→ `DownloadReconcileWorker` 周期对账 → `rename` 参数直接设置种子名 + 来源 tag，外部预置的同 hash 种子按 hash 接管）→ 响应丢失按 hash 对账确认 → 完成后按命名要素改名并记录替换关系：种子名沿用种子名称模板（只命名种子，不参与保存目录），内容文件使用文件名称模板并按各自实际集数渲染，媒体目录内的子目录结构只由文件模板中的 `/` 决定；附属文件挪进 `Seed/`；无命名要素的存量任务回退按命名快照改种子名。命名模板支持 `{ani.*}` 解析属性、`{bgm.*}` Bangumi 属性与 `{site.source}` 站点来源；旧 `{title}`、`{episode}` 等令牌继续兼容。删除任务时先删下载器再删记录（幂等可重试），可选连带删除下载器种子与已下载文件；被删任务替换的旧任务回退为已完成。

## 本地启动

首次使用空数据库时，必须通过环境变量提供首位管理员。应用会执行 SQLite 迁移、创建已确认的管理员账号，然后关闭开放注册。

PowerShell 示例：

```powershell
$env:Username = "admin"
$env:Password = "请替换为强密码"
dotnet run --project Banori/Banori.csproj
```

首次创建完成后应从运行环境移除 `BootstrapAdmin__Password`。默认数据库位于 `Banori/App_Data/mediamanager.db`，Data Protection 密钥位于 `Banori/App_Data/keys`；部署时二者都需要持久化和备份。

连接字符串中的普通相对数据库路径以应用内容根目录（`ContentRootPath`）为基准，不随进程工作目录变化；绝对路径保持原位置，SQLite `file:` URI 和内存数据库保留原有语义。注册确认页面已移除，不提供公开的邮箱确认链接。

### 数据库 provider：SQLite（默认）与 MySQL

应用长期支持两种数据库，由 `Database:Provider` 配置切换（`Sqlite` 默认 / `MySql`），迁移集按 provider 隔离：SQLite 迁移位于 `Data/Migrations/`，MySQL 基线位于 `Data/Migrations/MySql/`，由 `ProviderAwareMigrationsAssembly` 按当前 provider 过滤，互不可见。两者共用同一 EF 模型；"同一资源活动任务唯一" 的约束在 SQLite 用带 WHERE 的部分索引表达，在 MySQL 用等价的 `ActiveFlag` 存储型计算列（活动行 1、终态行 NULL，唯一索引不对 NULL 去重）。

MySQL 配置示例：

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Port=3306;Database=mediamanager;User=mediamanager;Password=请替换"
  },
  "Database": {
    "Provider": "MySql",
    "MySqlServerVersion": ""
  }
}
```

`MySqlServerVersion` 填服务器版本（如 `"8.0.36"` 或 `"10.11.4-MariaDB"`）可跳过启动探测；留空则启动时自动探测（要求启动时数据库可达）。MySQL provider 为 `Microting.EntityFrameworkCore.MySql`（Pomelo 的 EF Core 10 延续版，基于 MySqlConnector）。时间戳统一以 UTC 写入读取；MySQL `datetime(6)` 的精度为微秒（.NET tick 为 100 纳秒），应用内不存在亚微秒的时间戳相等比较，不受影响。

MySQL 下的迁移工具命令（快照文件的位置由工具自动管理，执行后请用 `git status` 检查 `Data/Migrations/ApplicationDbContextModelSnapshot.cs` 是否被重写为 MySQL 模型——工具会把快照写在默认位置；如被重写，从 git 恢复 SQLite 快照，并按 `Data/Migrations/MySql/` 内快照的提取方式同步 MySQL 快照）：

```powershell
dotnet ef migrations add <名称> --project Banori --startup-project Banori --namespace Banori.Data.Migrations --output-dir Data/Migrations
dotnet ef migrations add <名称> --project Banori --startup-project Banori --namespace Banori.Data.Migrations.MySql --output-dir Data/Migrations/MySql -- --Database:Provider=MySql
dotnet ef database update --project Banori --startup-project Banori -- --Database:Provider=MySql "ConnectionStrings:DefaultConnection=..."
```

schema 变更需要同时为两个 provider 各加一个迁移（模型唯一，迁移集两份）；用 `dotnet ef migrations has-pending-model-changes`（加同样的 `--` 参数）可以只读校验某一 provider 的快照是否与模型一致。

登录后可在“订阅”页面添加 Mikan RSS；RSS 地址使用 Data Protection 加密保存，页面不会显示查询参数。下载前先在“设置”页面启用并配置 qBittorrent（地址、账号、下载器侧保存路径，密码加密保存），优胜条目会自动登记下载任务；“下载任务”页面展示进度、失败步骤并支持暂停 / 恢复 / 重试 / 删除，删除支持单条与批量选择，均可选择是否连带删除 qBittorrent 中的种子及其已下载文件。数据库和密钥目录必须作为一个整体备份与恢复。

## 验证

```powershell
dotnet build Banori.slnx
dotnet test Banori.Tests/Banori.Tests.csproj
```

刷新任务持久化在数据库中：重复请求会复用等待中或执行中的工作；进程重启时把遗留的执行中工作恢复为待执行，已入库但未评估的条目会在下次刷新时继续处理，即使它们已不在最新 RSS 中。RSS 抓取失败不影响遗留条目的恢复，但本次任务会如实记录抓取失败。

下载任务同样持久化：提交响应丢失时进入“提交结果待确认”，下一轮按 info-hash 对账后再决定继续或重试；数据库唯一约束阻止同一资源在活动状态下重复登记；人工暂停不会被自动恢复；新版本资源在隔离路径完成后才把旧任务标记为被替换，旧任务与文件默认保留。下载器未配置或未启用时对账静默跳过，不会产生任何提交。

真实下载操作依赖外部 qBittorrent 与用户授权；测试套件不触发真实下载、文件删除或通知。
