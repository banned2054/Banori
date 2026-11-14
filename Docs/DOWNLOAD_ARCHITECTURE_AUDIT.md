# 下载系统架构审计（现状与证据）

> 实现核对：2026-11-14；发布准备更新：2026-11-15。实现基准为已提交的 v0.3.0 发布候选 `ec2cd71`。
>
> 审计对象：v0.2.4 之后已提交的来源隔离、下载恢复及展示实现。方法：直接读源码与测试、对照迁移文件；**2026-11-11 全量测试 1129/1129 通过，2026-11-14 复验 1130 通过 / 0 失败 / 0 跳过；2026-11-15 在 .NET 11 下以 Release 配置复验仍为 1130 通过 / 0 失败 / 0 跳过**；**已有真实 qBittorrent 5.3 隔离实例的部分行为实测记录（复检证据/checkingResumeData/pieces/应用重启/重复请求，见 §5、§8），做种链路由 2026-11-14 E2E 真机覆盖（见 §8）；MySQL 实机迁移、旧版本完整升级与备份恢复、双架构 .NET 11 容器构建及启动仍待验收，本轮仅补齐文档，未重跑实机验收**。
>
> 证据标记：**【源码】**= 直接读当前工作区源码；**【已有测试】**= 仓库内测试，已由本轮全量运行覆盖；**【报告】**= 过滤运行或补充运行记录（含真实 qB 隔离实例实测记录）；**【未验证】**= 需要真实 qB/MySQL，当前无证据。行号会随工作区变动，重要处同时给符号名。

## 1. 已修复与未修复（先看这张）

| 主题 | 当前事实 | 判定 |
|---|---|---|
| 上传未知来源 | `TorrentSiteSource.FallbackLabel = "other"`；`bt` 只在旧数据读取时归一为 `other` | 已修复【源码】 |
| RSS/上传判定分离 | RSS 只按 feed 地址主机；上传只按 tracker 表；多分类命中仍拒绝登记 | 已修复【源码】 |
| 订阅白名单漂移 | 白名单改为复用 `TorrentSiteSource.FromFeedHost`，`mikanime.tv` 可创建订阅 | 已修复【源码】 |
| 订阅批量择优跨源 | `SelectionGroupKey` 增加 `SourceScope`；组扫描、查询、唯一键、落库均带来源 | 已修复【源码】 |
| 逐集竞争跨源 | `ArbitrateEpisodesAsync` 先按 `SourceScope` 过滤占用者；登记表只读同源行 | 已修复【源码】 |
| 平级两层归约 | `SelectWinner` 改为两层归约：分区内版本冠军→分区冠军间计数决胜；现任仅计数严格更多被替换；`IsRevisionUpgrade` 已删除 | 已修复【源码+已有测试】 |
| 单 vs 多/多 vs 多数量 | 同优先级跨组/跨语言按初始正片数决胜（合集优先合集）；逐集独立，不做胜者全取（实现选择） | 已实现【源码+已有测试】 |
| 校验证据 | 发 recheck 前落库请求前基线（progress/amount_left/completed/completion_on/pieces/类别/raw），pieces_have 掉 0 记 `RecheckAppliedAtUtc`；真实 qB 5.3 实测同步清零再恢复 | 已实现【源码+已有测试+报告】 |
| 校验未确认 | `Step=Unconfirmed`：暂停、保文件、不隔离、可 `ReverifyAsync`；证据不足保持未确认（含全缺失+快速校验硬边界） | 已实现【源码+已有测试+报告】 |
| 做种/恢复分离 | `RunRecovery` JSON（Purpose/State）+ 双库迁移 + journal-first 收尾 + `AdvanceRunRecoveryAsync` 观察推进 + `ResumeRunAsync` 人工入口 | 已实现【源码+已有测试】 |
| 来源持久化 | `FeedItem`/`CandidateSelection`/`EpisodeVersion` 增加 `SourceScope`；`DownloadTask` 增加 `RunRecovery`；双库迁移 + 启动回填 | 已实现，MySQL 实机【未验证】 |
| 旧任务回填 | `EpisodeVersion` 按指针属主逐槽拆行；结构化报告（created/split/merged/idempotent/dangling/orphan/stale/conflicts/noPointer/ambiguous/nonCanonical/remaining/failures）；单行异常隔离 | 已实现【源码+已有测试】 |
| 文件视图/下一步 | 选择、承接、复用结果、等待原因、下一步进入投影/UI；恢复状态可回答“文件是否完整/种子是否恢复/在等什么”三问 | 已实现【源码+已有测试】 |
| AniBT 独立上传 | tracker 表无真实条目，仍归 `other` | 未修复（无值，不猜） |
| magnet 入口 | 无 UI、无服务方法、无 `magnet:` 解析 | 未实现 |
| 同 hash 跨来源 | 上传入口按 hash 全局查重，不按来源过滤 | 未修复（待业务定案） |
| 硬性过滤覆盖 | eligibility 仍只在订阅评估、订阅组择优、RSS 批内预选调用 | 未修复 |
| 完成期兜底守卫 | 同字幕组（忽略大小写）+ 同语言；命名缺组/语言时回退纯版本比较，限同一 `SourceScope` 行内。注意：`EpisodeVersionGuard_CrossGroupHigherVersion_DoesNotTakeOver`/`SameGroupDifferentLanguage` 两个用例中挑战方未列入规则（优先级 `int.MaxValue`），属**不同优先级层**，不覆盖“同层跨组”；同层跨组由 `EpisodeVersionGuard_CrossGroupLargerBatch_ReplacesUnparseableSingle` 与策略单测覆盖 | 已实现，注释口径已更正【源码+已有测试】 |
| 登记表媒体类型 | 唯一键仍为 `(SubjectId, Episode, SourceScope)`，不含 `MediaType` | 未修复 |
| 排序最终兜底 | `CandidateRankingPolicy` 分区内与冠军间两条链仍以 `FeedItemId`（DB Guid）收尾 | 未修复 |
| 订阅删除级联 | `SubscriptionService.DeleteAsync` 仍直接删除，FK 级联绕过引用回退 | 未修复 |
| 真实 qB/MySQL | qB 5.3 隔离实例已实测复检证据、checkingResumeData、pieces_have、应用重启、重复请求五项；做种链路已真机覆盖（2026-11-14 E2E，见 §8 报告条目）；`renameFile` 到已存在路径已真机验证（2026-11-14，见 §5）；Seeding 态选择下推、`content_path`、MySQL 实机迁移仍无证据 | 部分已测，其余【未验证】 |

## 2. 本版撤回的旧结论

- ~~“Core 版本策略同组无偏差”~~：旧结论随 `IsRevisionUpgrade` 删除而失效。现行策略是两层归约（分区→冠军→现任保留），不再有“升级判定”这一概念。
- ~~“现任只能被同组同语言的更高版本或更多计数替换，其余保留现任”~~：该口径会阻止“同优先级跨组/跨语言时合集优先”，已被用户 2026-11-11 规则取代——同优先级跨组按初始正片数决胜，现任仅当全局冠军计数严格更多才被替换。
- ~~“Checking 归属规则不可改、必须原样保留”~~：它只是实现级安全启发式，不是业务规则。现已改为“请求前基线 + pieces 掉落 + 完成见证 + 未确认态”。
- ~~“请求后的立即观察可接受任意 Checking（含 checkingResumeData）”~~：2026-11-09 版 D3 的例外窗口已撤回；`checkingResumeData` 现为统一规则——永不作内容校验证据，漏采由基线/pieces 证据补齐。
- ~~“快速校验漏采 → 暂停转人工（FastRecheck…Halts 类预期）”~~：两个旧用例的旧预期已按业务理由改为自动完成（证据已落库即自动收尾）。
- ~~“胜者全取已定”~~：用户只给了结果示例，没有确认胜者全取；当前实现不做胜者全取（实现选择，不是用户规则），包计数只用于平级决胜。
- ~~“历史登记可无损回填”~~、~~“回填整行三指针全有或全无”~~：两版口径均已撤回。现为按指针属主逐槽拆行：可证明的槽迁移、无法证明的槽保留 `(legacy)` 并报告，属于有损迁移，必须如实报告。

## 3. 来源分类与来源作用域【源码】

- `TorrentSiteSource.cs:12` 兜底 `other`；`:15-20` RSS 主机表（`mikanani.me`、`mikanime.tv`→`mikan`，`anibt.net`→`anibt`）；`:23-29` tracker 表只有 U2/BYRBT/TJUPT，无 AniBT；`:58-76` 主机全等 + `AbsolutePath` 全等，无子串匹配。
- `SourceScopes.cs:11-17` 定义 `(legacy)`/`(unknown)`/`other`；`:23-33` 归一（trim+小写、`bt`→`other`、空→`legacy`）；`:36-56` 从 feed 主机/URL、`SiteSource`、上传 tracker 推导作用域。
- RSS 一次性入口 `DownloadIntakeService.CreateFromRssFeedAsync` 用 `SourceScopes.FromFeedHost`；订阅入口 `FeedItemEvaluator` 解密 `FeedUrlProtected` 后写 `FeedItem.SourceScope`；建任务时优先用条目已落库的作用域，legacy 才回退解密。展示标签仍为 `SiteSource`，未知 RSS 为 null。
- 上传入口 `DownloadIntakeService.CreateFromUploadAsync` 先算展示标签，再 `SourceScopes.FromSiteSource` 得作用域；`ResolveUploadSiteSource` 命中多个不同分类时抛异常拒绝登记。
- 任务行没有 `SourceScope` 列；`DownloadService`、`DownloadFileService`、`DownloadTaskLookups` 统一用 `SourceScopes.FromSiteSource(task.SiteSource)` 派生。`TagsFor` 只写 `SiteSource`，未知 RSS 不写 tag，`other` 会写 tag。
- 【未验证】AniBT 真实 tracker 值；`magnet` 入口；同一 info-hash 跨来源并存。

## 4. 竞争、登记与包平级【源码】

- `CandidateRankingPolicy.SelectWinner`（`CandidateRankingPolicy.cs:76`）是**两层归约**：取规则优先级最高的候选层；层内按字幕组（忽略大小写）+ 语言（枚举全等，null 自成分区）分区；分区内按版本 → 初始正片数 → 发布时间 → 稳定标识 → `FeedItemId` 选分区冠军（`OrderWithin`，`:108`）；分区冠军间按初始正片数 → 发布时间 → 稳定标识 → `FeedItemId` 选全局冠军（`OrderAcross`，`:116`）。现任不在最高层时取全局冠军；在最高层时取现任所在分区的冠军，**仅当全局冠军初始正片数严格更多才替换**（`:96-98`），计数相同保留现任侧。版本只在同分区内比较，不定义跨分区比较器；不按组名/语言名排序；`IsRevisionUpgrade` 已删除。
- `EpisodeCandidate.PackageVideoFileCount`（`EpisodeCandidate.cs:35`）注释明确：只数正片、不含字幕/Seed、跨包落选不缩小、不是独立排序维度。
- `DownloadIntakeService.BuildEpisodeScopes` 在逐集裁决前算 `InitialVideoFileCount(task)`（`DownloadIntakeService.cs:884`），写入同一包的每个 `EpisodeScope`；占用者候选（`OccupantCandidate`）也按同一函数取值。因此平级比较使用“初始值”，不依赖处理顺序。
- `ArbitrateEpisodesAsync`（`:610`）先构造同源占用者，再 1:1 对决；来源过滤在 `:649`，登记表读取在 `:730-733`；`ApplyWinningEpisodesAsync` 按 `(SubjectId, Episode, SourceScope)` 查/建行，`SourceScope` 写入在 `:942-951`。
- `ApplyLosingSelections`/`AllContentLost`/职责耗尽逻辑仍是“部分落选只取消对应文件、全部内容失去职责才整包退出”；整包退出 `deleteFiles=false`。
- **包含承接仍在**：`IsContainmentTakeover`（`:796`）与有界剔除循环（`:752-782`）保留，可在“已完成现任 + 同集正片同名同大小 + 全职责覆盖 + 严格超集”时翻转常规落选。它是业务基准之外的窄例外，处置见业务 U3。
- 上传入口在仲裁前按 hash 查重：`DownloadIntakeService.cs:147-151` 调 `DownloadTaskLookups.FindActiveDuplicateIdAsync`/`FindCompletedDuplicateIdAsync`；查询不按 `SiteSource` 过滤，若已有活动/已完成/墓碑任务，同 hash 跨来源会被拒绝。仲裁内另有同 hash 跳过（`:648`）。
- 硬性过滤未全局化：`CandidateEligibilityPolicy` 的生产调用点仍只有 `FeedItemEvaluator`、`CandidateSelectionService.PlanSingleGroupAsync`、`DownloadIntakeService.PlanBatchSelection`；包内变体决胜、跨包裁决、上传、RSS 单条路径不执行。
- 完成期 `SwitchEpisodeIncumbentsAsync` 已按任务来源只读写同源行（`DownloadService.cs:712` 起）；`DownloadTaskLookups.IsTakeoverEligibleIncumbent` 要求同字幕组（忽略大小写）/同语言，命名缺组/语言时回退纯版本守卫。该兜底只覆盖“非登记在案优胜”的路径；`DownloadEpisodeLifecycleRegressionTests.EpisodeVersionGuard_*` 中，`CrossGroupHigherVersion_DoesNotTakeOver`/`SameGroupDifferentLanguageHigherVersion_DoesNotTakeOver` 的挑战方未列入规则（`UnmatchedPolicy` 兜底 `int.MaxValue`），属不同优先级层；“同层跨组按计数替换”由 `EpisodeVersionGuard_CrossGroupLargerBatch_ReplacesUnparseableSingle`（跨组合集替换不可解析单集，旧“跨组保留现任”预期已按用户规则改写）与 `CandidateRankingPolicyTests` 覆盖。

## 5. 复用校验与 qB 适配【源码+已有测试+报告】

- `TorrentSnapshot` 含 `RawState`、`AmountLeft`、`Completed`、`CompletionOn`、`ContentPath`、`AddedOn`、`LastActivity`、`SeedingTime`（`IQbittorrentSession.cs:33-40`）；另有 `TorrentPropertiesSnapshot`（`torrents/properties` 子集，含 `PiecesHave`/`PiecesNum`，`:43-47`）。
- **请求前基线**：`IssueRecheckAndObserveAsync`（`DownloadService.cs:1366`）在发送 recheck 前捕获并随证据落库请求前基线（progress/amount_left/completed/completion_on/pieces_have/pieces_num/类别/原始状态，`:1438-1446`），发送后立即再查一次。发送抛 qB/网络异常记 `SendFailed`、不消耗 `IssuedCount`，连续失败达到 `MaxRecheckSendFailures=3` 才转未确认。
- **`RecheckApplied` 是“请求作用于该 hash”的最可靠证据**：基线 `pieces_have>0` 且请求后掉 0 时记 `RecheckAppliedAtUtc`（`:1505-1507`）。真实 qB 5.3 隔离实例实测：recheck 请求后 qB 同步清零 pieces 再恢复为校验结果（首读可见）；HTTP 200 不构成可靠证据。属性查询失败时无 `RecheckApplied`（`DownloadRecheckEvidenceTests`：属性查询失败、无 `RecheckApplied` 的分支已覆盖）。
- **完成见证**：基线未完成 → 请求后进入停止态且完成（跨稳定窗口观察一致），或 `RecheckApplied` + 完成，才可确认复用/完整。
- **基线完整的三条路径**：基线完整 → 请求后完整性下降 = 校验确已执行且缺失 → 确认缺失 → 隔离；基线完整 → 仍完整且无 `RecheckApplied` → 保持 `Unconfirmed`（基线完整仍发 recheck，以便发现文件被移走/缺失）；全缺失 + 快速校验无法证明执行 → 保持 `Unconfirmed`（硬边界）。缩短轮询间隔只能降低漏采概率，不构成证明。
- **`checkingResumeData` 统一规则**：任何路径都不把 `checkingResumeData` 当内容校验证据（含请求刚发出的窗口，2026-11-09 的例外窗口已撤回）；它是添加/启动/重启产物，不改变 progress/pieces_have。旧意图/会话没有 `RawState` 时按保守处理。
- **不叠加请求**：请求前基线若已是 `checkingDL`/`checkingUP`，本轮不再发 recheck（真实 qB 实测：对正在校验的种子重复 recheck 可卡死校验）。
- **应用重启作废证据**：证据带进程级 `ObserverInstanceId`；重启后失配即作废本轮证据并重新发送，不带病沿用。
- `EnterVerificationUnconfirmedAsync`（`:1484`）：置 `Step=Unconfirmed`、暂停、系统暂停、清自动观察；不隔离、不删种、不重下。`ReverifyAsync`（`:381`）重置本轮证据、`RequestState=NotSent`、轮次 +1，保留候选与文件。
- **做种收尾改 journal-first**（详见决策 D5）：确认完整后先落 `RunRecovery=AwaitingConfirm` 再发 `Resume`；`Resume`/`Reannounce` 失败只记录，不改 `Status`/`FailedStep`/`Error`/`CompletedAtUtc`。`ReannounceAsync` 已加入 `IQbittorrentSession`/`QbittorrentSession`。
- 【报告】`renameFile` 到已存在路径已真机验证（2026-11-14，见 [DOWNLOAD_TARGET_ARCHITECTURE.md](DOWNLOAD_TARGET_ARCHITECTURE.md) G1.1；rename 另有 `.e2e/tools/probe_rename.py` 探针记录）：rename onto 已有真实文件 = 纯元数据变更、目标文件内容完好、recheck 后判定完整；场景证据在 `.e2e/s1/`、`.e2e/s2/`。
- 【未验证】Seeding 态文件选择下推、`content_path` 语义与缺失、Reannounce 实际响应。

## 6. 数据模型、迁移与回填【源码】

- `ApplicationDbContext`：`FeedItem.SourceScope` NOT NULL 默认 `(legacy)`，新增非唯一索引 `(SubjectId, MediaType, Episode, SourceScope)`；`CandidateSelection` 唯一键 `(SubjectId, MediaType, Episode, SourceScope)`；`EpisodeVersion` 唯一键 `(SubjectId, Episode, SourceScope)`，仍不含 `MediaType`；`DownloadTask.RunRecovery` 可空 JSON 列（`DownloadTask.cs:92`，`DownloadRunRecovery` record 见 `DownloadModels.cs:456`）。
- 迁移：SQLite `20261109102357_AddSourceScopes`、MySQL `20261109102428_AddSourceScopes`（`bt`→`other` 归一 SQL）；新增 SQLite `20261111144219_AddDownloadRunRecovery`、MySQL `20261111144238_AddDownloadRunRecovery`；存量行 `RunRecovery=null`（= 没有待恢复的启动，`RunRecoveryMigrationTests` 覆盖升级与 Down）。
- `SourceScopeBackfill`（`SourceScopeBackfill.cs`）在 `ApplicationInitializer` 的 `MigrateAsync` 后、后台 worker 启动前运行：
  - `FeedItem`：解密订阅地址成功才回填，失败保留 `(legacy)`；
  - `CandidateSelection`：按优胜条目的来源回填，目标组键已占用则保留 `(legacy)` 并告警；
  - `EpisodeVersion`：**按指针属主逐槽拆行**——逐槽解析 file→task→`SiteSource`：空来源记 `(unknown)` 并计 `AmbiguousSiteSource`；非规范标签只报告 `NonCanonicalSiteSource`、不归并；悬空指针排除并报告；孤儿 `Superseded`/陈旧 `Pending` 不迁移；目标键已存在时仅允许“完全一致（幂等）”或两种只填 null 的受控合并，否则冲突不覆盖；成功槽从 legacy 行清空（旧行保留），天然幂等。
  - `RunAsync` 签名不变；`RunWithReportAsync` 返回结构化报告（created/split/merged/idempotent/dangling/orphan/stale/conflicts/noPointer/ambiguous/nonCanonical/remaining/failures）；单行异常隔离，不阻断启动。
- 因此“旧数据全部无损”不成立：可证明的槽拆行迁移，其余隔离并报告。旧任务按自身 `SiteSource` 派生作用域（为空时派生 `(unknown)`），读不到遗留 `(legacy)` 行的 Pending/Incumbent，完成时会另建新行。这是当前兼容边界；无法证明来源的槽以 `(legacy)`+报告收尾是否为终态，见业务 U4。
- `DownloadRunRecovery`：`Purpose`=Seeding/ContinueDownload；`State`=AwaitingConfirm/ResponseUnknown/ConfirmedRunning/FailedRetryable/UserPaused/DownloaderStopped。`AdvanceRunRecoveryAsync`（`DownloadService.cs:1823`）观察推进：运行→`ConfirmedRunning`（ContinueDownload 确认后清记录回普通下载）；Sent 后停止→`DownloaderStopped` 不自动拉起；`ResponseUnknown` 有界自动重试（`MaxRunResumeAttempts=3`，窗口=`ControlConfirmationWindow`）；用户暂停→`UserPaused`。人工入口 `ResumeRunAsync`（`:1947`）仅 `FailedRetryable`/`DownloaderStopped` 可用。恢复路径不调 Recheck/隔离/重下，不 `ApplySnapshot`、不参与完成判定。

## 7. 文件视图与可解释性【源码+已有测试】

- `DownloadFileView` 新增 `Size`、`IsSelectedForDownload`、`SelectionReason`、`SupersededByTaskId`、`SupersededByNaming`、`VerificationOutcome`；`DownloadFileService.ViewAsync` 一次批量查承接方命名，文件面板显示“不下载”、落选原因、承接方、复用结果、大小。
- `DownloadTaskView` 新增 `NextObservationAtUtc`、`AttemptCount`、`DownloaderReady`、`VerificationRequestState`，以及派生 `IsSelectionTombstone`、`WaitingForText`、`NextStepText`；恢复状态独立投影（`DownloadTaskQueries.cs:273` 读 `RunRecovery`）。“三问”可分别回答：文件是否完整（`Status`/`VerificationOutcome`）、种子是否恢复（`RunRecovery`）、在等什么/如何恢复（`RunState`/`Reason`/`NextStepText`）。
- 这些属性只读持久字段，不被推进分支读取；`DownloadTaskQueries` 对“未确认”只做筛选归属，不改变状态。`Replaced` 的“同集落选墓碑”和“已下载后被替代”在展示上已分开。
- 【未实现】“最近一次远端操作/响应”仍只有时间戳、次数、`RequestState` 与错误串；`AddTorrent` 的响应文本、改名/移动的实际回执不落库。

## 8. 验证状态（单测全绿 ≠ 实机已验证）

- 【测试】2026-11-15，基于 `ec2cd71` 的 .NET 11 实现运行 `dotnet test Banori.Tests/Banori.Tests.csproj --no-restore -c Release -v minimal`：**1130 通过 / 0 失败 / 0 跳过**。本次仅为本地回归，不包含真实 qB、MySQL、浏览器或容器升级验收。
- 【报告】真实 qBittorrent 5.3 隔离实例（独立端口/save path/配置目录）已实测：recheck 请求后 `pieces_have` 同步清零再恢复、`checkingResumeData` 的出现与语义、请求前已有 `checkingDL`/`UP` 时重复 recheck 可卡死校验、应用重启后证据作废路径。这些实测支撑 §5 的证据规则。
- 【报告】2026-11-14 两个跨站搬运复用 E2E 验收场景真机通过（隔离 qBittorrent 4.6.4 容器 + 真实 Program/worker + 独立 SQLite，`.e2e/Banori.E2E.Tests/CrossSeedAcceptanceScenarios.cs`）：非受管候选复用全链与 v2 映射修正命名 onto 被占用目标路径，均 `ReusedVerified`→`ConfirmedRunning`→Completed、下载增量逐样本为 0、qB 终态 stalledUP。§5 的“受控映射改名 + recheck 复用”链路由此获得 4.6.4 实机证据；证据在 `.e2e/s1/`、`.e2e/s2/`，复跑入口见 `.e2e/README-e2e.md`。
- 【未验证】真实 qB 其余行为：qB 4.x/5.x 文件 index、Seeding 态选择下推、`content_path`、Reannounce 响应；真实 MySQL 实机迁移。
- 【未验证】0.2.4 存量数据副本的完整升级与配套密钥、加密订阅及任务恢复，升级前数据库和配置备份的回退演练，以及 .NET 11 容器的 `linux/amd64`、`linux/arm64` 构建和启动。已有 E2E 使用独立 SQLite，不能代替旧库升级验收；具体待办见 [qBittorrent 后续任务](QBITTORRENT_FOLLOW_UP.md#6-页面数据库与容器升级)。
- 【测试】2026-11-11 本轮 `dotnet test Banori.slnx --no-restore -v minimal`：**1129 passed / 0 failed / 0 skipped**；`dotnet build Banori.slnx` 成功。新增/改写的测试覆盖：两层归约与顺序无关（`CandidateRankingPolicyTests`、`DownloadPackageRankingTests`）、跨组合集替换不可解析单集（`EpisodeVersionGuard_CrossGroupLargerBatch_ReplacesUnparseableSingle`）、复检基线证据/pieces/重启（`DownloadRecheckEvidenceTests`）、做种/恢复分离状态机（`DownloadRunRecoveryTests`、`DownloadVerificationRecoveryTests`）、逐槽拆行回填与结构化报告（`SourceScopeBackfillTests`、`DownloadLegacyHandoffTests`）、RunRecovery 迁移（`RunRecoveryMigrationTests`）、快速校验自动完成（`DownloadVerificationTests.FastRecheckCompletesBetweenSamples_*`，旧“漏采→暂停人工”预期已改）。
- 【报告】审计目录过滤运行记录只对应当次范围，不能替代全量结论；最近本地全量回归为 2026-11-15 .NET 11 / Release 复验，1130 通过 / 0 失败 / 0 跳过。2026-11-14 的 1130 项及 2026-11-11 的 1129 项记录保留为历史证据。

*本文只记录现状，不提出新方案；业务要求见 [DOWNLOAD_BUSINESS_RULES.md](DOWNLOAD_BUSINESS_RULES.md)，取舍见 [DOWNLOAD_ARCHITECTURE_DECISIONS.md](DOWNLOAD_ARCHITECTURE_DECISIONS.md)，剩余目标见 [DOWNLOAD_TARGET_ARCHITECTURE.md](DOWNLOAD_TARGET_ARCHITECTURE.md)。*
