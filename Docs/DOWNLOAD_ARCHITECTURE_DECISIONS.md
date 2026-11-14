# 下载系统架构决策（DOWNLOAD_ARCHITECTURE_DECISIONS）

> 最后核对：2026-11-11，基于当前工作区未提交实现；未提交/未发布。
>
> 本文只记录**已采纳的决策**与**已拒绝的方案**，不写阶段历史，不保留第二个“最终方案”。业务要求见 [DOWNLOAD_BUSINESS_RULES.md](DOWNLOAD_BUSINESS_RULES.md)，现状证据见 [DOWNLOAD_ARCHITECTURE_AUDIT.md](DOWNLOAD_ARCHITECTURE_AUDIT.md)，剩余目标见 [DOWNLOAD_TARGET_ARCHITECTURE.md](DOWNLOAD_TARGET_ARCHITECTURE.md)。

## 0. 统一术语

| 术语 | 本仓库含义 |
|---|---|
| 来源作用域 `SourceScope` | 竞争键域：已知来源小写标签、`(unknown)`（未知 RSS）、`other`（未知上传）、`(legacy)`（迁移旧行） |
| 展示标签 `SiteSource` | 任务上的站点标签，用于展示、命名 `{source}` 与 qB tag；不等于竞争键 |
| 临时目录 | 替换暂存根 `.tasks/{taskId}`；只有存在同来源低优先级待替换时才使用 |
| 复用校验 | 停止态受控映射 + recheck 证据（请求前基线、pieces 掉落、完成见证）；路径+大小只是候选线索 |
| `RecheckApplied` | 基线 `pieces_have>0` 且请求后掉 0 → 本轮 recheck 确已作用于该 hash 的最可靠证据 |
| 现任/待完成 | `EpisodeVersion` 的 `IncumbentVideoFileId` 与 `PendingVideoFileId`，只在同一 `SourceScope` 行内读写 |
| 初始候选正片数 | 逐集裁决前包内 `Anime` 正片数；不含字幕/Seed，不随后续落选缩小 |
| 启动恢复 `RunRecovery` | 任务行上的可空 JSON（Purpose/State）：完成后做种/继续下载的恢复子状态，与数据完整性正交 |

## D1 来源作用域：哨兵、唯一索引、应用层逐槽拆行回填

**决策**

- `FeedItem`、`CandidateSelection`、`EpisodeVersion` 各加 `SourceScope`：`NOT NULL`、`maxLength=50`、默认 `(legacy)`；`CandidateSelection` 唯一键扩为 `(SubjectId, MediaType, Episode, SourceScope)`，`EpisodeVersion` 扩为 `(SubjectId, Episode, SourceScope)`；`FeedItem` 只加普通索引。
- 不新增任务级来源列；`DownloadTask.SiteSource` 仍是展示/tag 事实源，竞争作用域由 `SourceScopes.FromSiteSource` 派生。
- `SourceScopes` 是唯一归一入口：`(legacy)`/`(unknown)`/`other` 三哨兵；未知 RSS 用 `(unknown)`，未知上传用 `other`，两者永不互竞；历史 `bt`→`other`，空值读取→`(legacy)`，写入不产生 NULL。
- 迁移只做 SQLite/MySQL 加列、换索引、`DownloadTasks.SiteSource` 的 `bt`/大小写归一；数据回填走启动时 `SourceScopeBackfill`，不放进 EF 迁移。
- 回填按指针属主**逐槽拆行**：逐槽解析 file→task→`SiteSource`；空来源记 `(unknown)` 并报告；非规范标签只报告不归并；悬空排除并报告；孤儿 `Superseded`/陈旧 `Pending` 不迁移；目标键已存在仅允许完全一致（幂等）或只填 null 的受控合并，否则冲突不覆盖；成功槽从 legacy 行清空（旧行保留）。回填必须可重放、可报告（`RunWithReportAsync` 结构化报告）、失败不阻断启动。

**理由**

- SQLite 与 MySQL 的唯一索引都把 NULL 视为互不相等；含 NULL 的来源列无法保证“每组每源一行”，且 `=` 无法匹配 NULL。
- 不拆 `SourceEpisodeHolder + LibraryEpisode` 两张表：当前没有第二个消费方，单表加来源即可完成隔离，避免第二份必须同步的账本。
- 迁移无法解密 `FeedUrlProtected`，也无法安全处理跨来源拆行；应用层回填可重放、可报告、失败不阻断启动。
- 整行“全有或全无”会把一条多来源行整体锁死在 `(legacy)`，可证明的槽也被隔离，违背“能确定来源尽量迁移、无法确定明确报告”（用户明确，2026-11-11）；逐槽拆行配合“成功槽清空 legacy 行”天然幂等。

**替代方案（已拒绝）**

- `SourceScope` 可空 + 复合唯一索引：两库都不能保证唯一。
- 只给 `EpisodeVersion` 加一个来源列、保留全局唯一键：三重职责不解除，跨源完成切换与时序缺口仍在。
- 新建 `SourceEpisodeHolder` + `LibraryEpisode`：收益未证实，回填/双写/双读成本高，当前不做。
- 迁移 SQL 内解密订阅地址或拆跨源行：迁移不具备解密能力，双 dialect 复杂逻辑易错。
- 删除数据库重建、给 `DownloadTask` 再加一份来源列：制造第二份事实或丢数据风险。
- 整行“三指针全有或全无”回填（2026-11-09 口径）：多来源行整体不可迁移，可证明关系被无谓隔离，已被逐槽拆行取代。

**影响**

- 所有竞争读点（登记、完成切换、复用提前切换、迁让、职责/退休）只读写同源行；跨来源旧行被隔离到 `(legacy)`，新来源不会继承其 Pending/Incumbent。
- 旧任务按自身 `SiteSource` 派生作用域（为空时派生 `(unknown)`），读不到遗留 `(legacy)` 行的 Pending/Incumbent，完成时会另建新行；无法证明来源的槽保留 `(legacy)` 并报告，是否为终态待业务 U4 定案。
- MySQL 实机升级、真实旧库的回填分布仍未验证。

**状态：已实现（应用层逐槽拆行 + SQLite/MySQL 迁移 + 回填/迁移测试，2026-11-11 全量 1129/1129 通过）；MySQL 实机未做，无法证明槽的终态待 U4 定案。**

## D2 平级两层归约：分区冠军 + 计数决胜；不做 DC_opt/胜者全取

**决策**

- 同集组内择优取规则优先级最高的候选层，做**两层归约**：层内按字幕组（忽略大小写）+ 语言（精确）分区；分区内按版本 → 初始候选正片数 → 发布时间 → 稳定标识选分区冠军；分区冠军之间按初始候选正片数 → 发布时间 → 稳定标识选全局冠军。
- **版本只在同一分区内比较**；不定义跨分区比较器，不按字幕组名/语言名排序。跨组/跨语言同优先级按初始正片数决胜（用户明确，2026-11-11：优先级相同时单集与合集优先合集；组合/通配不得由“保留现任”阻止合集优先）。
- **现任保留**：现任不在最高层时取全局冠军；在最高层时取现任所在分区的冠军，仅当全局冠军初始正片数**严格更多**才替换；计数相同保留现任侧（现任本身，或其分区内版本/计数更高的升级者）。
- 初始候选正片数只数 `Anime`，不含字幕/Seed，逐集裁决前取值，不随后续落选缩小；单集 vs 合集、合集 vs 合集同一口径。
- 逐集独立裁决，部分落选只取消对应文件；全部内容失去职责才整包退出。**不做胜者全取（实现选择，不是用户规则）**；不引入 `DC_opt`、`FinalDC`、`PackageOrderKey K1–K8`、迭代不动点或“按最终保留数决胜”。
- `IsRevisionUpgrade` 已删除：成对“升级判定”被归约取代，避免全序假设下的循环。
- 两条决胜链末尾仍以 `FeedItemId`（DB ID）兜底，仅在前面的键全部相同时生效（实现现状，未确认为规则；见业务 U5）。

**理由**

- 排序链要求全序，而“现任保留”破坏全序：会出现“v2 优于 v1、现任 v1 优于他组、他组按组名优于 v2”的循环。归约把“现任保留”从比较器中拿出，只保留一次计数比较，消除循环。
- 用户 2026-11-11 规则：同分类才竞争；先比配置的字幕组/语言优先级；修订版本只在同组同语言内；同优先级单集与合集优先合集、合集间按初始候选正片数；组合/通配造成跨组/跨语言同优先级时不得由“保留现任”阻止合集优先。
- 初始候选正片数是输入的纯函数：跨包落选不缩小它，因此不受登记顺序、逐集处理顺序、最终胜负影响。
- “按最终胜出数”自指，容易产生多个自洽结果；当前方案直接消除该循环，且保留逐文件语义。

**替代方案（已拒绝）**

- 旧口径“现任只被同字幕组 + 同语言的更高版本或更多计数替换，其余保留现任”（2026-11-09 口径）：跨组/跨语言同优先级时阻止合集优先，与用户 2026-11-11 规则冲突，已删除。
- 按字幕组名/语言名排序决胜：组名与语言名不是业务信号，结果随命名漂移。
- `DC_opt` + 胜者全取 + K1–K8 完全序：会成组判给胜者，违反逐文件裁决与“部分落选只取消对应文件”的语义；`DC_opt` 也不是用户规则。
- 迭代不动点式“反复按最终胜出数重算直到收敛”：不动点不唯一，无法定义唯一答案。
- 原始 torrent 文件总数、只数非对决集、包级贪心：都丢失或篡改了逐文件信号。
- 以字幕/Seed 计入包计数：与用户确认的口径冲突。
- 继续按“登记顺序”决定单 vs 多、多 vs 多：结果依赖登记顺序，与用户已明确的平级规则冲突。

**影响**

- `CandidateRankingPolicy.SelectWinner`（`CandidateRankingPolicy.cs:76`）实现两层归约；`EpisodeCandidate.PackageVideoFileCount`、`DownloadIntakeService` 逐集裁决前的初始计数、占用者同口径不变。
- 同层跨组场景由 `EpisodeVersionGuard_CrossGroupLargerBatch_ReplacesUnparseableSingle`（跨组合集替换不可解析单集，旧“跨组保留现任”预期已按用户规则改写）与 `CandidateRankingPolicyTests` 覆盖；完成期守卫用例 `EpisodeVersionGuard_CrossGroupHigherVersion_DoesNotTakeOver`/`SameGroupDifferentLanguageHigherVersion_DoesNotTakeOver` 中挑战方未列入规则（`int.MaxValue`），属不同优先级层，不覆盖同层跨组——相关注释已更正。
- 包含承接仍是业务基准之外的窄例外；`FeedItemId` 兜底未内容键化。

**状态：已实现（两层归约 + 顺序无关 + 跨组合集用例，2026-11-11 全量 1129/1129 通过）；`FeedItemId` 兜底、包含承接未闭环。**

## D3 校验证据：请求前基线 + pieces 掉落 + 完成见证；checkingResumeData 统一规则

**决策**

- 发 recheck 前捕获并落库**请求前基线**（progress/amount_left/completed/completion_on/pieces_have/pieces_num/类别/原始状态），发送后立即再查一次；发送抛 qB/网络异常记 `SendFailed`、不消耗 `IssuedCount`，连续失败达到上限（`MaxRecheckSendFailures=3`）才转未确认。
- **`RecheckApplied` 是“请求作用于该 hash”的最可靠证据**：基线 `pieces_have>0` 且请求后掉 0 即记录 `RecheckAppliedAtUtc`（真实 qB 5.3 实测：请求后 qB 同步清零 pieces 再恢复）；HTTP 200 不可靠。
- **完成见证**：基线未完成 → 请求后停止态完成（跨稳定窗口一致），或 `RecheckApplied` + 完成，才确认复用/完整。
- **基线完整的三条路径**：完整性下降 = 校验执行且缺失 → 确认缺失 → 隔离；仍完整且无 `RecheckApplied` → 保持 `Unconfirmed`（基线完整仍发 recheck，以便发现文件移走/缺失）；全缺失 + 快速校验无法证明执行 → 保持 `Unconfirmed`（硬边界）。
- **`checkingResumeData` 统一规则**：任何路径（含请求刚发出的窗口）都不把 `checkingResumeData` 当内容校验证据；它是添加/启动/重启产物，不改变 progress/pieces_have。
- **不叠加请求**：请求前已有 `checkingDL`/`checkingUP` 时不发本轮 recheck（真实 qB 实测：重复 recheck 可卡死校验）。
- **应用重启作废证据**：证据带进程级 `ObserverInstanceId`，重启失配即作废本轮证据并重发。
- 缩短轮询间隔只能降低漏采概率，**不构成证明**；证据不足时一律进入未确认保护（暂停、保文件、不隔离、不重下、可 `ReverifyAsync`）。
- 完整性确认后的做种恢复走 journal-first（决策 D5），不在本校验决策内。

**理由**

- 旧链路发出 recheck 后首次采样默认约 15 秒，快速校验常在两次采样之间结束，结构性漏采；“有没有看到 Checking”不可靠，必须换成请求作用于该 hash 的直接证据。
- 真实 qB 5.3 隔离实例实测支撑：pieces_have 同步清零再恢复、`checkingResumeData` 与本次 recheck 无关、重复 recheck 卡死校验、重启后证据跨进程失效。
- “请求发送失败”与“请求已发但结果未知”必须分开：前者不应消耗请求预算，后者不能断言未发送。
- 未确认是“数据未知”，不是“数据损坏”；隔离/重下会破坏用户已有数据，必须由用户选择重新校验或独立下载。

**替代方案（已拒绝）**

- 旧 D3“请求后的立即观察接受任意 Checking（含 `checkingResumeData`）”的例外窗口：实测 `checkingResumeData` 与本次 recheck 无关，会产生假阳性；已被统一规则取代。
- 维持“Checking 归属规则原样、不可改”：把实现启发式当成业务不变量，无法修复快速校验漏采。
- 未采样到 Checking 时凭 `progress==1` 或重复 100% 直接判 `ReusedVerified`：没有独立的“请求已生效”证据。
- 引入 `amount_left`/`completion_on` 作为替代“本轮执行”证据：字段保留作交叉核对，但均不构成“请求作用于该 hash”的直接证据；pieces 掉落才是。
- 未确认时自动隔离/重加/重下：违反“未确认不删不隔离”，且会破坏已有文件。
- 把 reannounce/tracker 可达性作为完整性前置或否决条件：两者正交。
- 靠缩短轮询间隔解决漏采：只降低概率，不构成证明。

**影响**

- `DownloadService`：`IssueRecheckAndObserveAsync` 基线捕获与 `RecheckAppliedAtUtc`（`DownloadService.cs:1438`、`:1505`）、`EnterVerificationUnconfirmedAsync`、`ReverifyAsync`、`ObserverInstanceId`；`IQbittorrentSession` 增加 `TorrentPropertiesSnapshot`（pieces）与 `ReannounceAsync`；`DownloadModels` 增加 `Unconfirmed`、`RequestState`、`SendFailureCount`、`LastRequestError`、`ObservedRawState`；UI 增加“重新校验”。
- 旧的 `Failed("复用校验")` 出口改为暂停未确认；数据不完整与操作失败仍走原路径，避免把未知当损坏。
- 覆盖测试：`DownloadRecheckEvidenceTests`（pieces 掉落/属性查询失败/中断校验基线）、`DownloadVerificationTests`/`DownloadVerificationPhaseThreeTests` 的快速校验自动完成（旧“漏采→暂停人工”预期已按业务理由改为自动完成）。

**状态：已实现（fake qB + 真实 qB 5.3 隔离实例实测复检证据/checkingResumeData/pieces/重启/重复请求，2026-11-11 全量 1129/1129 通过）；`renameFile` 到已存在路径已真机验证（2026-11-14，见 DOWNLOAD_TARGET_ARCHITECTURE.md G1.1）；MySQL 实机、Seeding 态下推/Reannounce 实机仍未验证。**

## D4 单写者与解释性：最小方案

**决策**

- 保留 `DownloadService` 作为已存在任务的生命周期协调者，不拆十个模块、不引入第二套状态机、不做 `PauseKind + Stage` 大重构、不建 append-only 远端操作表。
- 解释性只做“读现有持久字段”的最小增量：`DownloadFileView` 增加选择/承接/复用结果/大小；`DownloadTaskView` 增加下次观察、尝试次数、下载器就绪、请求状态，以及 `WaitingForText`/`NextStepText`/`IsSelectionTombstone` 派生文本；恢复状态独立投影，使“文件是否完整/种子是否恢复/在等什么”三问可分别回答。
- 展示属性只读持久字段，不得被推进分支读取；失败步骤用静态建议表，未知步骤给兜底文案。
- 远端操作仍以现有时间戳、计数、`RequestState`、错误串解释；最近一次操作的持久化留作剩余目标 G4，形态优先单字段 JSON（随任务行、覆盖写），不建 append-only 表。

**理由**

- 当前缺的不是新状态机，而是把已落库的 `SelectionReason`、`SupersededByTaskId`、`VerificationIntent`、`VerificationOutcome`、`ArchiveReview`、`FailedStep`、`RunRecovery` 暴露出来。
- 拆服务/拆表/新台账会引入第二份必须同步的事实，成本和回归面大于收益。
- 单写者方向保留：`DownloadService` 是运行期任务字段的主写者，`DownloadIntakeService` 只在创建与登记事务内写择优副作用，`DownloadFileService` 只管布局/归档。

**替代方案（已拒绝）**

- M1–M10 十模块目标架构、`ReleaseIdentity`/`ArenaKey`/`PackageDecision` 全量落地：本轮不做，避免为设计而设计。
- `SourceEpisodeHolder + LibraryEpisode` 双表：见 D1。
- `PauseKind(None/User) + Stage` 替代双布尔：收益是结构收敛，风险是接管场景 both-true 语义丢失；本轮不改。
- 新增 append-only 远端操作表或 `PathDecision` 历史表：没有真实工单支撑，迁移/清理/UI 成本不成比例。
- 用 UI 推断“文件是否该下载/任务是否卡住”：展示层不得猜内部状态。

**影响**

- 文件面板能回答“这个文件下不下、为什么、被谁承接、复用是否证实”；列表能回答“在等什么、下一步、下次观察”，恢复状态能回答“种子是否恢复、如何恢复”。
- 仍未做：`UpdateTaskAsync` CAS 失败静默、集中不变量检查、`DownloadFileService` 停止写任务级字段、最近远端操作审计。
- 没有改变任何竞争/校验语义；新增字段均为只读投影。

**状态：部分实现（文件视图、下一步、请求状态、恢复投影已完成；单写者收敛与远端操作审计未做）。**

## D5 做种/继续下载恢复分离：RunRecovery journal-first

**决策**

- **数据完整与启动恢复分离**：文件是否完整（`Status`/`VerificationOutcome`）、种子是否恢复（`RunRecovery`）、在等什么/如何恢复（`RunState`/`Reason`/`NextStepText`）三个问题各自独立回答（用户明确，2026-11-11）。
- **完成收尾 journal-first**：完整性确认后，先把 `RunRecovery` 落为 `AwaitingConfirm`，再发 `Resume`；`Resume`/`Reannounce` 失败只记录，**绝不改** `Status`/`FailedStep`/`Error`/`CompletedAtUtc`，不否定已确认的完整性，也不得因此调 Recheck、隔离或重下。
- **形态**：`DownloadTask.RunRecovery` 单字段可空 JSON（随任务行、覆盖写）；`Purpose`=Seeding/ContinueDownload；`State`=AwaitingConfirm/ResponseUnknown/ConfirmedRunning/FailedRetryable/UserPaused/DownloaderStopped；null = 没有待恢复的启动。
- **观察推进**（`AdvanceRunRecoveryAsync`）：运行 → `ConfirmedRunning`（ContinueDownload 确认后清记录回普通下载）；Sent 后停止 → `DownloaderStopped`，不自动拉起；`ResponseUnknown` 有界自动重试（`MaxRunResumeAttempts=3`，窗口=`ControlConfirmationWindow`），耗尽转 `FailedRetryable`；用户暂停 → `UserPaused`，停止自动重试。
- **人工入口** `ResumeRunAsync`：仅 `FailedRetryable`/`DownloaderStopped` 可用；其余状态拒绝。
- 恢复路径不 `ApplySnapshot`、不参与完成判定；对账 worker 把 `Completed+RunRecovery` 纳入继续观察。

**理由**

- 用户明确规则：复检确认完整后自动继续做种、不重复下载；Resume/Reannounce 失败不否定完整性；响应丢失先观察下载器实际状态，不凭响应丢失直接失败。
- fire-and-forget 的 Resume 无法回答“种子是否恢复”；先落记录再请求使“请求已发、结果未知”成为可观察、可重试的状态，而不是丢失的操作。
- 无界自动重试会反复打扰下载器；有界重试 + 人工兜底把“自动能做的”和“必须人决定的”分开。
- `DownloaderStopped` 不自动拉起：发送成功后外部又停止，往往意味着用户或下载器有意停止，自动重启会对抗用户意图。

**替代方案（已拒绝）**

- 完成即 fire-and-forget `Resume` + `Reannounce`、失败仅告警（2026-11-09 口径）：失败后无状态可观察，“种子是否恢复”永远答不出；已被 journal-first 取代。
- `ResponseUnknown` 直接判失败并隔离/重下：响应丢失 ≠ 失败，且会重复下载用户已确认完整的数据。
- 无界自动重试直到成功：对下载器的无界打扰，失败原因不可收敛。
- 新状态机或 `PauseKind + Stage` 重构承载恢复状态：见 D4，单字段 JSON + 覆盖写已够用，出现“需要连续多次请求历史”的真实工单再评估。

**影响**

- `DownloadTask.RunRecovery`（`DownloadTask.cs:92`）、`DownloadRunRecovery`（`DownloadModels.cs:456`）；SQLite/MySQL 迁移 `20261111144219`/`20261111144238`（存量行 null）；`DownloadTimingPolicy.MaxRunResumeAttempts`；`DownloadService.StartRunWithRecoveryAsync`/`IssueRunResumeAsync`/`ConfirmRunRunningAsync`/`AdvanceRunRecoveryAsync`/`ResumeRunAsync`；对账 worker（`DownloadReconcileWorker.cs:125`）纳入 `Completed+RunRecovery`；视图三问文案。
- 覆盖测试：`DownloadRunRecoveryTests`（Resume 失败/响应丢失确认/有界重试耗尽/外部停止/人工恢复/重启续观察/部分完成分支/用户暂停/三问投影/人工入口门槛）、`DownloadVerificationRecoveryTests`、`DownloadVerificationPhaseOneRegressionTests`、`DownloadVerificationPhaseThreeTests`。

**状态：已实现（fake qB 测试，2026-11-11 全量 1129/1129 通过）；做种链路已真机覆盖（2026-11-14 跨站搬运复用 E2E，见 DOWNLOAD_TARGET_ARCHITECTURE.md G1.1）。**

*本文只保留已采纳/已拒绝的决策；未定案问题在业务基准 §9，不在本文另立“推荐方案”。*
