# 下载系统剩余目标（DOWNLOAD_TARGET_ARCHITECTURE）

> 实现核对：2026-11-14；发布准备更新：2026-11-15。实现基准为已提交的 v0.3.0 发布候选 `ec2cd71`。
>
> 测试状态：2026-11-15 在 .NET 11 下以 Release 配置复验 **1130 通过 / 0 失败 / 0 跳过**（命令见 [架构审计](DOWNLOAD_ARCHITECTURE_AUDIT.md#8-验证状态单测全绿--实机已验证)）；2026-11-14 回归复验同为 1130 通过。已有真实 qB 5.3 隔离实例的复检证据/checkingResumeData/pieces/重启/重复请求五项实测及 2026-11-14 qB 4.6.4 跨站复用 E2E 记录；MySQL 实机、旧库完整升级与备份恢复、双架构 .NET 11 容器构建及启动仍待验收，本轮未重跑实机验收。
>
> 本文只写**剩余目标、职责边界与验收矩阵**，不重复业务规则、不再给十模块大设计。业务要求见 [DOWNLOAD_BUSINESS_RULES.md](DOWNLOAD_BUSINESS_RULES.md)，现状证据见 [DOWNLOAD_ARCHITECTURE_AUDIT.md](DOWNLOAD_ARCHITECTURE_AUDIT.md)，已采纳取舍见 [DOWNLOAD_ARCHITECTURE_DECISIONS.md](DOWNLOAD_ARCHITECTURE_DECISIONS.md)。

## 1. 目标与边界

- 不重写下载系统：保留当前 `SourceScope` + 两层归约平级裁决 + 逐集逐文件独立 + 单协调器 + journal-first 文件层/恢复层的结构，只补齐验证与兼容边界。
- 剩余工作分三类：**实机验证**（真实 qB 未测行为、真实 MySQL；做种链路已真机覆盖，见 G1.1）、**旧数据兼容**（无法证明来源槽的终态定案、跨源旧 holder）、**解释性与新入口**（最近远端操作、AniBT tracker、magnet、同 hash 跨源）。
- 完成标准：每项都有可复现证据；未跑过的实机项必须标“未验证”，不得用 fake 测试替代真机结论。
- 不在本轮引入（已拒绝，见决策 D2/D4/D5）：第二套状态机、`DC_opt`/胜者全取、`SourceEpisodeHolder + LibraryEpisode` 双表、十模块拆分、append-only 操作历史表。

## 2. 已实现 / 未实现

| 能力 | 当前状态 |
|---|---|
| 来源作用域三列 + 唯一键 + 启动回填 | 已实现（SQLite 迁移/回填测试；MySQL 仅离线脚本） |
| RSS/上传分类、订阅白名单、未知哨兵 | 已实现（`SourceScopes` 单点归一） |
| 订阅批量择优按来源拆组 | 已实现（`SelectionGroupKey` 含 `SourceScope`） |
| 逐集竞争按来源隔离、登记表按来源读写 | 已实现（登记/完成/复用提前切换/迁让/职责） |
| 平级两层归约（分区冠军 + 计数决胜 + 现任严格更多替换）；逐集独立（不做胜者全取，实现选择） | 已实现（策略 + 包计数 + 顺序无关 + 跨组合集用例） |
| 复检证据：请求前基线 + `RecheckApplied`（pieces 掉落）+ 完成见证 + 重启作废 + 不叠加请求 | 已实现（fake qB 测试 + 真实 qB 5.3 部分实测） |
| 未确认态、重新校验、checkingResumeData 统一规则 | 已实现（fake qB + 部分实测） |
| 做种/继续下载恢复分离（`RunRecovery` journal-first + 有界重试 + 人工入口） | 已实现（fake qB 测试；做种链路已真机覆盖，见 G1.1） |
| 旧任务逐槽拆行回填 + 结构化报告 | 已实现（`SourceScopeBackfill.RunWithReportAsync`） |
| 文件选择/承接/复用结果/下一步展示；完整性/恢复/等待三问投影 | 已实现（投影 + UI） |
| 真实 qB 集成验证 | **部分完成**：复检证据/checkingResumeData/pieces/重启/重复请求已实测；`renameFile` 到已存在路径已真机验证（2026-11-14，见 G1.1）；Seeding 态选择下推、`content_path`、Reannounce 响应未实测 |
| 做种/恢复链路实机验证 | **部分完成**：做种链路已真机覆盖（2026-11-14，见 G1.1） |
| 真实 MySQL 迁移验证 | **未做** |
| 跨来源旧 holder（`(legacy)`）兼容 | **部分**：逐槽拆行已实现；无法证明来源槽保留 `(legacy)` 的终态待业务 U4 定案 |
| 最近远端操作审计 | **未做** |
| AniBT tracker 真实值 | **未做**（无值，不猜） |
| magnet 入口 | **未做** |
| 同 hash 跨来源 | **未定案/未改** |
| eligibility 全局覆盖、`MediaType` 入登记键、去 `FeedItemId` 兜底 | **未做** |
| 完成期组/语言守卫（命名缺组/语言时回退版本） | 已实现（`EpisodeVersionGuard_*`；跨组/跨语言用例属不同优先级层，同层跨组由策略单测与跨组合集用例覆盖） |

## 3. 剩余目标

### G1 真实 qBittorrent 验收

- 已实测（qB 5.3 隔离实例，2026-11-11）：recheck 请求后 `pieces_have` 同步清零再恢复、`checkingResumeData` 出现时机与语义、请求前已有 `checkingDL`/`UP` 时重复 recheck 卡死校验、应用重启后证据作废、复检证据链（基线→`RecheckApplied`→完成见证）。对应结论已可标“已实测”。
- 仍待实测：qB 4.x/5.x 文件 index 与 padding 过滤、Seeding 态文件选择下推、`content_path` 语义与缺失、`completion_on`/`amount_left` 在 recheck 后的变化细节、`Reannounce` 的实际响应。
- 验收：逐项形成“输入/操作/qB 返回/结论”记录；任何一项未测，对应结论保持“未验证”，不得写成“已支持”。
- 不接触用户真实库；不改生产数据。

#### G1.1 跨站搬运复用 E2E 验收（2026-11-14 真机通过）

- 两个真实端到端验收场景已在 `.e2e/Banori.E2E.Tests/CrossSeedAcceptanceScenarios.cs` 实现并真机通过（隔离 qBittorrent 4.6.4 容器 + 真实 Banori Program/worker + 独立 SQLite）：
  - 场景1（CrossSeedScenario1）：目标命名已正确的非受管候选复用全链——登记→发现候选→受控映射改名 onto 候选路径→recheck→`ReusedVerified`→`ConfirmedRunning`→Completed。
  - 场景2（CrossSeedScenario2）：旧任务 C 先走真实完整链完成并占住目标路径，v2 新种子逐集裁决胜出后映射修正命名 onto 被 C 占用的目标路径→recheck→`ReusedVerified`→现任切换→`ConfirmedRunning`→Completed，C 终态 Replaced。
- 关键断言在真实 qB 上执行：同内容不同 piece-length 产生互异 info-hash；下载增量逐样本为 0；qB 终态 stalledUP（Seeding 类）。
- 独立复验通过：干净复跑复现、证据一致、主解决方案 build 0 警 0 错、Banori.Tests 1130/0。
- 复跑入口与前置见 [.e2e/README-e2e.md](../.e2e/README-e2e.md)；证据落盘 `.e2e/s1/`、`.e2e/s2/`（summary.json / evidence.jsonl / final.jsonl / torrent-manifest-*.json）。

### G2 MySQL 实机迁移

- 在可丢弃的 MySQL 实例执行 `MigrateAsync`：验证三列 NOT NULL/默认值、两个新唯一键、`RunRecovery` 列、`SourceScope` 大小写与 collation、`Episode` decimal 键行为、`bt→other`/大小写归一 SQL。
- 验收：升级后旧行数不变、同组同源唯一冲突被拒、不同源可并存；`Down` 只在“尚未写入新来源行”的干净副本上验证；生产回退必须备份。
- 离线模型/脚本测试不能替代本项。

### G3 跨来源旧 holder 兼容与回填终态

- 已实现：`EpisodeVersion` 回填按指针属主逐槽拆行——逐槽解析 file→task→`SiteSource`；空来源记 `(unknown)` 并报告；非规范标签只报告不归并；悬空排除并报告；孤儿 `Superseded`/陈旧 `Pending` 不迁移；目标键已存在仅允许完全一致（幂等）或只填 null 的受控合并，否则冲突不覆盖；成功槽从 legacy 行清空（旧行保留），天然幂等；结构化报告（created/split/merged/idempotent/dangling/orphan/stale/conflicts/noPointer/ambiguous/nonCanonical/remaining/failures）；单行异常隔离不阻断启动。
- 剩余：按业务 U4 定案“无法证明来源的槽保留 `(legacy)` 并报告”是否为终态，以及受控合并边界是否认可；定案前不扩大拆行范围、不删除任何旧行。
- 验收：旧任务完成/回退/退休不产生跨源引用；拆行后每条新行只有一个来源；报告悬空/多来源/冲突数量；回填重复运行结果一致。

### G4 最近远端操作审计

- 只解决“最近一次发了什么、下载器返回什么、是否可能已送达”：`Kind`（Add/Pause/Resume/Recheck/Rename/SetLocation/SetFileSelection/Delete/Query）、`Target`、`Attempt`、`Result`（Sent/Succeeded/Rejected/Failed/Unknown）、有界 `Response`/`Error`、`AtUtc`。
- 形态优先单字段 JSON（随任务行、覆盖写、无清理任务）；只有出现“需要连续多次请求历史”的真实工单再评估 append-only 表。
- 验收：`Add` 成功/5xx、recheck 发送失败、rename 被拒三类可区分；`Unknown` 必须存在；不写完整 feed URL/passkey；旧任务显示“未记录”。

### G5 AniBT tracker 真实值

- 由用户提供真实 announce 主机/路径；加入 `TorrentSiteSource.TrackerCategories` 并补“同分类多地址去重/多分类拒绝/AniBT 上传识别”测试。
- 未提供前：独立上传仍归 `other`，与 AniBT RSS 的 `anibt` 分域；文档与 UI 说明“AniBT 上传暂未识别”，不猜、不写占位值。

### G6 magnet 入口

- 定义入口与解析：`magnet:` URI → info-hash + 可选 tracker；来源仍只按上传 tracker 表分类，未知归 `other`；不读 RSS 地址；同 hash 查重沿用现有规则（U2 定案后再调整）。
- 验收：无 tracker 的 magnet 归 `other`；多分类仍拒绝；无真实 qB 时只做解析/登记层测试，不宣称下载成功。

### G7 同 hash 跨来源

- 按业务 U2 定案：若“hash 是全局资源身份”，保持现查重并补拒绝原因/UI 说明；若“来源内身份”，查重加 `SourceScope`，并处理 qB 端同 hash 只能有一个种子时的接管、tag、来源展示与复用关系。
- 验收：两种定案各有直接用例；不出现“同 hash 两个任务竞争同一 qB 种子”的未定义状态。

### G8 竞争边界收口

- eligibility 在进入竞争的所有候选上统一执行（上传、RSS 单条、包内变体、跨包裁决），并明确整包全 Ineligible 的处置。
- `EpisodeVersion` 是否把 `MediaType` 纳入键；`CandidateRankingPolicy` 两条决胜链末尾的 `FeedItemId` 兜底是否改为内容键（业务 U5 定案）。完成期“非登记优胜”守卫已补字幕组/语言（命名缺组/语言时回退版本），不再列入待定。
- 验收：每项先定案再改；未定案前保持现状并在审计文档标注，不得静默改变结果。

## 4. 职责边界

| 层 | 负责 | 不负责 |
|---|---|---|
| `Banori.Core`（`TorrentSiteSource`/`SourceScopes`/`CandidateRankingPolicy`/`CandidateEligibilityPolicy`） | 来源分类与归一、纯规则排序/过滤（两层归约） | 数据库、qB、文件 IO |
| `DownloadIntakeService` | 入口判定、同 hash 查重、逐集仲裁应用、登记表写入、初始候选正片数计算 | 已存在任务的生命周期推进、启动恢复 |
| `DownloadService` | 已存在任务的生命周期：提交/准备/校验/隔离/完成/退休/启动恢复（`RunRecovery`）；运行期字段主写者 | 文件布局细节、竞争规则本体 |
| `DownloadFileService` | 文件身份、journal-first 改名/移动、归档/迁让/占用检查 | 竞争胜负、登记表语义、恢复请求 |
| `QbittorrentSession` / `IQbittorrentSession` | qB API 唯一出口、原始快照与属性快照、recheck/reannounce | 业务判断、重试策略 |
| `DownloadTaskQueries` / Blazor 展示 | 只读投影、等待/下一步/恢复文案（三问分离） | 推断状态、写任何推进字段 |

## 5. 验收矩阵

> 2026-11-15 在 .NET 11 下以 Release 配置复验 **1130 通过 / 0 失败 / 0 跳过**，A1–A5 均在本地回归范围内。B 类按各行记录区分部分实测、未验证、未做及未定案，不能以回归通过替代实机验收。

| # | 验收项 | 证据/方法 | 状态 |
|---|---|---|---|
| A1 | 来源作用域隔离（订阅/登记/完成/复用/退休） | SQLite + fake qB 用例 | 本轮全量通过 |
| A2 | 平级两层归约（分区/计数/现任严格更多）、顺序无关；完成期组/语言守卫 | `CandidateRankingPolicyTests`、`DownloadPackageRankingTests`、`EpisodeVersionGuard_*`（含跨组合集替换用例） | 本轮全量通过 |
| A3 | 复检证据（基线/`RecheckApplied`/见证/重启作废/不叠加请求）、未确认/重新校验、`RunRecovery` 恢复分离 | `DownloadRecheckEvidenceTests`、`DownloadVerificationPhaseThreeTests`、`DownloadRunRecoveryTests`、`DownloadVerificationRecoveryTests` | 本轮全量通过（fake）+ 复检证据五项真实 qB 5.3 已实测 |
| A4 | 迁移与回填（SQLite：SourceScopes + RunRecovery + 逐槽拆行） | `SourceScopeMigrationTests`、`RunRecoveryMigrationTests`、`SourceScopeBackfillTests`、`DownloadLegacyHandoffTests` | 本轮全量通过 |
| A5 | 迁移脚本（MySQL 离线） | `MySqlSourceScopeScriptTests` | 离线测试通过/未执行实机 |
| B1 | 真实 qB 行为 | G1 隔离实例记录 + G1.1 E2E | **部分验证**：复检证据/checkingResumeData/pieces/重启/重复请求已实测；做种链路已真机覆盖（2026-11-14，见 G1.1）；`renameFile` 到已存在路径已真机验证（2026-11-14，见 G1.1）；Seeding 态下推、`content_path`、Reannounce 未实测 |
| B2 | 真实 MySQL 升级/唯一键/collation | G2 可丢弃实例 | **未验证** |
| B3 | 跨来源旧 holder/`(legacy)` 终态 | G3 定案（U4）+ 回填/完成/回退用例 | **部分**：逐槽拆行已实现并有测试；终态待业务定案 |
| B4 | 最近远端操作审计 | G4 字段 + 三类请求用例 | **未做** |
| B5 | AniBT tracker | G5 真实值 + 分类用例 | **未做** |
| B6 | magnet 入口 | G6 解析/登记用例 | **未做** |
| B7 | 同 hash 跨来源 | G7 定案 + 查重/接管用例 | **未定案** |
| B8 | eligibility/MediaType/DB ID | G8 逐项定案 + 等价测试 | **未做** |
| B9 | 0.2.4 旧库升级、备份回退与双架构 .NET 11 容器 | [后续验收清单](QBITTORRENT_FOLLOW_UP.md#6-页面数据库与容器升级)，使用数据副本和隔离容器 | **未验证**：本轮仅补齐文档，未执行实机验收 |

*本文只列剩余目标与验收口径；业务规则冲突时以业务基准为准，已采纳取舍见决策文档。*
