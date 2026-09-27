# MediaManager

MediaManager 是面向个人或家庭媒体库的动漫 RSS 管理面板。当前已完成 P0、P1、P2、P2R 和 P3：可以管理 Mikan 订阅、持久化刷新任务、去重保存条目、关联 Bangumi Subject，展示筛选理由与命名预览，并在条目详情页把已选择的条目登记为下载任务，由后台提交 qBittorrent、按 info-hash 对账直至完成改名。

## 项目结构

```text
MediaManager.Core/        筛选、集数修正、命名预览、info-hash 提取、路径净化等纯规则，可独立测试
MediaManager/
  Components/Pages/       订阅、条目、下载任务、设置页面及页面 ViewModel
  Features/               业务服务：订阅管理、Mikan 刷新、条目查询、总览、下载闭环、设置
  Data/                   EF Core 实体与 SQLite 迁移
  Integrations/           Mikan、Bangumi、qBittorrent 客户端与种子下载
  Workers/                周期调度 Worker（订阅刷新、下载对账）
MediaManager.Tests/       规则单元测试、SQLite 行为测试
```

刷新链路：订阅页 / 到期调度 → `SubscriptionService` 入队 → `MikanRefreshService` 执行（读 RSS、去重入库、评估遗留条目）→ `FeedItemEvaluator`（AniParser 解析、Subject 关联、Core 规则决策、命名预览）→ 保存结果与下次刷新时间。

下载链路：条目详情页创建下载意图（下载种子、提取 info-hash、登记 `DownloadTask`）→ `DownloadReconcileWorker` 周期对账 → 提交 qBittorrent（隔离子目录 + 分类）→ 响应丢失按 hash 对账确认 → 完成后按命名快照改名并记录替换关系。

## 本地启动

首次使用空数据库时，必须通过环境变量提供首位管理员。应用会执行 SQLite 迁移、创建已确认的管理员账号，然后关闭开放注册。

PowerShell 示例：

```powershell
$env:BootstrapAdmin__Email = "admin@example.com"
$env:BootstrapAdmin__Password = "请替换为强密码"
dotnet run --project MediaManager/MediaManager.csproj
```

首次创建完成后应从运行环境移除 `BootstrapAdmin__Password`。默认数据库位于 `MediaManager/App_Data/mediamanager.db`，Data Protection 密钥位于 `MediaManager/App_Data/keys`；部署时二者都需要持久化和备份。

连接字符串中的普通相对数据库路径以应用内容根目录（`ContentRootPath`）为基准，不随进程工作目录变化；绝对路径保持原位置，SQLite `file:` URI 和内存数据库保留原有语义。注册确认页面已移除，不提供公开的邮箱确认链接。

登录后可在“订阅”页面添加 Mikan RSS，在“条目”页面检查解析与决策；RSS 地址使用 Data Protection 加密保存，页面不会显示查询参数。下载前先在“设置”页面启用并配置 qBittorrent（地址、账号、下载器侧保存路径，密码加密保存），再在已选择条目的详情页创建下载任务；“下载任务”页面展示进度、失败步骤并支持暂停 / 恢复 / 重试。数据库和密钥目录必须作为一个整体备份与恢复。

## 验证

```powershell
dotnet build MediaManager.slnx
dotnet test MediaManager.Tests/MediaManager.Tests.csproj
```

刷新任务持久化在数据库中：重复请求会复用等待中或执行中的工作；进程重启时把遗留的执行中工作恢复为待执行，已入库但未评估的条目会在下次刷新时继续处理，即使它们已不在最新 RSS 中。RSS 抓取失败不影响遗留条目的恢复，但本次任务会如实记录抓取失败。

下载任务同样持久化：提交响应丢失时进入“提交结果待确认”，下一轮按 info-hash 对账后再决定继续或重试；数据库唯一约束阻止同一资源在活动状态下重复登记；人工暂停不会被自动恢复；新版本资源在隔离路径完成后才把旧任务标记为被替换，旧任务与文件默认保留。下载器未配置或未启用时对账静默跳过，不会产生任何提交。

真实下载操作依赖外部 qBittorrent 与用户授权；测试套件不触发真实下载、文件删除或通知。
