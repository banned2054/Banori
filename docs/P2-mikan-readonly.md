# P2 Mikan 只读闭环验收

日期：2026-09-12

## 已完成范围

- 管理员可以新建、编辑、启停、删除 Mikan 订阅，并创建立即刷新任务。
- RSS 地址使用 Data Protection 加密保存，页面只显示移除查询参数后的地址。
- 首条真实后台 Worker 每 15 秒调度到期订阅，并消费持久化刷新工作。（P2R 更新：业务服务已并入宿主项目 Features/ 目录，独立的 Application 项目已移除。）
- Mikan RSS 原始条目先落库，再通过“订阅 ID + 稳定 GUID / 规范化链接 / 内容摘要”唯一键去重。
- 标题直接交给 Banned.AniParser 0.6.0；业务库只保存筛选、查询和审计需要的字段。
- Subject 优先使用订阅指定值或已缓存标题映射，否则从 Mikan 条目页和番剧页提取 bgm.tv Subject ID。
- Subject 详情只通过 Banned.Bangumi 1.1.0 获取并缓存，不自行调用 Bangumi HTTP API。
- 集数修正、1080p / 字幕组 / 简中筛选复用 Core 中的 P0 规则，并保存结构化状态、偏好和中文理由。
- 为每个已解析且已关联的单集生成安全命名预览；P2 不下载 torrent，也不注册 qBittorrent 客户端。
- 总览、订阅、条目列表和条目详情页已接入真实数据；详情页支持人工指定 Subject 后重新评估。

## 失败与恢复语义

- RSS 获取失败只令本次工作失败，保留订阅、历史条目和最近错误；下个周期可重新调度。
- 单条解析或关联失败不会中止整批，条目进入待处理或失败状态并保留解释。
- 重复点击立即刷新会复用当前等待中或执行中的工作；重复抓取同一条目不会重复入库。
- 进程中断后遗留的执行中工作会在五分钟租约超时后由 Worker 重新领取。（已被 P2R 更新取代：改为启动时恢复为待执行，正常运行不再按耗时重领。）
- 解析失败、多集合集、缺失集数或 Subject 无法关联都不会产生“已选择”结果。

## 本地验证

- `dotnet build Banori.slnx`：0 个警告，0 个错误。
- `dotnet test Banori.Tests/Banori.Tests.csproj`：16 个测试全部通过。
- 测试覆盖 SQLite 空库迁移、RSS XML 与 Mikan 页面解析、真实 AniParser 决策链、RSS 地址脱敏、命名预览和重复抓取去重。
- 真实宿主启动冒烟（2026-09-13）：在隔离的临时内容目录中以空 SQLite 数据库、随机端口和一次性 `BootstrapAdmin` 环境变量启动编译产物；三个迁移依次应用、首位管理员创建成功、`SubscriptionRefreshWorker` 完成至少两个批次且无错误；未登录访问 `/` 被重定向到 `/Account/Login`。验证后已停止进程并删除临时目录，未触碰仓库内 `App_Data`。

---

# P2R 更新（2026-09-13）

P2R 为可读性整理阶段：架构收敛与行为补齐，不改变 P2 的功能与数据语义。以上 P2 验收内容为历史记录，其中被标注的两条语义已由本节取代。

## 结构收敛结果

- 解决方案收敛为 Core、宿主、Tests 三个项目；Application / Client 项目及其空目录已移除，业务代码位于宿主 `Features/`、`Data/`、`Integrations/`、`Workers/` 与 `Components/Pages/`。
- 统一存储接口（IMediaCatalogStore）与纯转发类已删除；查询与写入落在具体服务：`SubscriptionService`（订阅 CRUD、校验、加密、入队与到期调度）、`MikanRefreshService`（单次刷新主线与启动恢复）、`FeedItemEvaluator`（刷新与人工重评共用的评估）、`FeedItemQueries` 与 `DashboardQueries`。
- `FeedItemAnalysis`、`FeedItemView` 改为具名属性的 record，必填字段用 `required` 标注，失败分支不再传位置 null。
- 订阅、条目、设置页面使用页面 ViewModel（普通属性 + Load/Save 方法），Razor 不访问数据库或外部客户端；ViewModel 随页面组件创建释放，未注册为 Scoped/Singleton。
- Data Protection 用途字符串 `Banori.MediaSubscriptions.FeedUrl.v1`、既有实体、迁移与加密密文均未改动。

## 行为修正结果

- 五分钟租约超时重领已移除，改为启动恢复：消费者运行前把上次进程遗留的 Running 工作恢复为 Pending（`MikanRefreshService.RecoverInterruptedWorksAsync`），正常运行不因耗时而重领；恢复成功前 Worker 不进入消费循环，恢复失败（如临时数据库错误）时以 2 秒起、上限 60 秒的退避等待重试，直到成功或进程停止，避免遗留工作既无法领取又阻止重新入队。
- 手动请求与到期调度共用 `RefreshEnqueueGate` 串行化“检查无活动工作 → 插入工作”，防止并发交错创建多个活动工作。
- 条目评估在入库后进行，读取数据库中全部 Pending 条目（含已离开最新 RSS 的遗留条目），RSS 抓取失败不阻塞恢复、工作如实以抓取失败收尾。
- Subject 确定顺序为指定值 → 标题映射缓存 → Mikan 页面发现；指定值或缓存命中不访问 Mikan 页面。

## 验收结果（本次实测）

- `dotnet build Banori.slnx`：0 个警告，0 个错误。
- `dotnet test Banori.Tests/Banori.Tests.csproj`：22 个测试全部通过（含启动恢复、遗留 Pending 条目恢复、Subject 缓存命中跳过页面发现、RSS 失败仍恢复遗留条目、手动与到期并发入队去重、运行中不重领、空库迁移）。
- 验收中发现并修复 1 个缺陷：`MikanRefreshService.EvaluatePendingItemsAsync` 对 `FirstSeenAtUtc` 使用 SQLite 服务端 `OrderBy`，EF Core SQLite 抛出 `NotSupportedException`，导致所有带条目的刷新以“意外错误”收尾；已改为取回后内存排序，与代码库其余 DateTimeOffset 排序一致。
- 验收复查（2026-09-13）发现并修复 1 个缺陷：启动恢复仅在 `ExecuteAsync` 开头调用一次，恢复失败后虽记录日志但直接进入消费循环，遗留 Running 工作既不能被领取又会阻止同一订阅重新入队，订阅会卡住到下次重启；已改为恢复成功后才进入消费循环，失败时退避等待并重试（Worker 改经 `IRefreshWorkConsumer` 调用刷新服务），并补充“首次恢复失败、随后成功，成功前不消费”的 Worker 测试。修复后 23 个测试全部通过，构建 0 警告 0 错误。
- 真实宿主页面冒烟（2026-09-13）：隔离临时内容目录 + 空 SQLite 库 + 本地回环 RSS 服务启动宿主；未登录访问重定向登录页；登录后新建启用订阅（回环地址 + 固定 Subject #42），后台刷新成功，条目页显示 2 条（1 条 Accepted 含解析快照与命名预览，1 条 NeedsReview 附原因），详情页人工指定 Subject 重评复用同一规则并更新决策与命名预览；Bangumi 元数据真实获取并写入缓存与标题映射。验证后进程与临时目录均已清理，仓库内 `App_Data` 未被触碰。
