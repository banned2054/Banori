# P4 前置：批量同集择优与下载意图衔接 实施记录

日期：2026-09-26。

状态：批量择优核心链路（比较语义、数据模型、批量流程、下载意图衔接、中断恢复）已实施，本地测试全部通过；规则配置编辑页面、保存前比较预览与规则接入设置存储已实施（2026-09-27，见下文"规则配置与比较预览"），全量 144 个测试通过（0 警告 0 错误）；真实 qBittorrent 端到端验证待完成（P3 起遗留）；迁移副本验证与离线回放待实施。

## 实施范围

### 比较语义（Core，`MediaManager.Core/Rules/Selection/`）

- `SelectionRuleConfig`：独立模式（字幕组顺序 + 语言组合顺序 + 维度顺序）与组合模式（有序条件，首个命中确定优先级，可显式通配兜底）；语言完全 / 包含匹配（`LanguageMatchMode`）；未命中策略（`UnmatchedPolicy`：待确认或兜底参与）；硬性过滤（允许分辨率，空 = 不限制）；规则版本号随配置递增。
- `LanguageComposition`：AniParser 语言组合的基础语言展开，仅用于“包含指定语言”判断；多语言不天然优于单语言，排序只依据配置列表序。
- `CandidateEligibilityPolicy`：硬性过滤。多集合集进入待确认；分辨率不在允许范围则排除并记录原因。排名较低的字幕组与语言不在此提前排除。
- `CandidateRankingPolicy`：现任优胜的保留**不进入排序链**（排序链要求全序；把现任保留放进链内会与“同组版本升级”“跨组按组名稳定排序”形成循环，导致优胜随输入顺序变化），而是显式判定：取规则优先级最高的候选层；现任在该层内时，只有同字幕组的更高修订版本构成严格升级，其余情况保留现任；现任不在该层时按组名 Ordinal → 修订版本（同组内生效）→ 发布时间早者优先 → 稳定来源标识的基础全序取第一。不使用 RSS 枚举顺序或数据库 ID 决胜。
- 原 `DefaultReleasePolicy` 移除，“ANi + 已有 Accepted → Rejected”旧固定接受条件废除；`ReleaseDecisionStatus` 枚举保留。

### 状态语义分离

- `FeedItem.Status`（Pending/Accepted/Rejected/NeedsReview/Failed）收窄为解析与硬性过滤结果：Accepted = 合格候选，可参与同集竞争，不再是最终下载选择。
- 组内选择：新表 `CandidateSelections`，组键唯一（SubjectId + MediaType + Episode），保存优胜条目、规则版本、优胜依据、命名快照、意图任务 Id 与意图失败原因。
- 条目级标记：`FeedItem.SelectionOutcome`（None/Winner/RunnerUp），落选候选保留解析快照与原因，不永久拒绝。
- 任务状态：沿用 P3 `DownloadTask.Status`。

### 批量流程

- 评估（`FeedItemEvaluator`）只做解析、Subject 关联、集数修正、硬性过滤与命名预览；不再查询“已有 Accepted”。
- `MikanRefreshService` 在整批评估完成后收集本批合格条目的分组键，调用 `CandidateSelectionService.SelectAndRegisterAsync` 统一择优。批次边界 = 单订阅一次刷新；候选查询覆盖该组全部历史与跨订阅条目。
- 人工重评（`ReevaluateAsync`）评估后触发受影响组重选；条目改组时旧组与新组都重选。
- 条目详情页展示同集选择结果（当前优胜 / 组内落选 / 尚未参与）。

### 下载意图衔接

- 选择记录与下载任务在同一本地短事务落库（`CommitSelectionAsync`）；种子下载等网络请求在事务外。
- 同集组内同一轮只登记一个优胜意图。同 hash 已存在任意状态任务（含失败、被替换）时不重复登记；同条目活动任务直接复用引用。
- 手动创建入口（条目详情页）与自动登记共用同一门槛：条目必须是所在组当前优胜；同 hash 已完成任务不重复创建。
- 下载器未配置时只落库选择记录，意图留空，配置后由恢复扫描自动补齐。

### 中断恢复

- `RecoverPendingSelectionsAsync`（挂在 `DownloadReconcileWorker` 每轮）覆盖两类窗口：已合格但所在组尚无选择记录的条目（择优计算后、落库前中断，即使条目已不在最新 RSS 也能恢复）；选择记录存在但意图未登记且无失败原因。
- 三道防线避免恢复扫描把评估顺序变成下载顺序：存在执行中刷新工作时整体跳过；候选条目所属订阅仍有待评估（Pending）条目时跳过该组——覆盖“批次评估中途中断、工作已被启动恢复转为 Pending、尚未重新消费”的窗口，防止按部分候选先下载低优先级版本；下载器未启用或未配置时直接返回。
- 优胜变化时选择记录的意图字段（IntentTaskId / IntentFailedReason）先重置再按结果赋值：换优胜后旧任务引用与旧失败原因不再保留，恢复扫描据此为新优胜补登记。
- 种子获取或登记失败时写入 `IntentFailedReason` 并保留选择记录，恢复扫描不自动重试，等待人工处理；人工重评（`ReevaluateAsync`）以 `resetFailedIntent: true` 显式要求重试。
- 条目重新评估后不再合格（Rejected / NeedsReview / Failed）时清除旧的组内落选标记（SelectionOutcome 置 None），详情页不再显示已失效的“组内落选”；仍合格的条目保留原值等待组内重选更新。

## 2026-09-27 review 修复

- 恢复扫描：Pending 等待规则改为按组判定（组内任一候选所属订阅仍有 Pending 条目即整组等待，跨订阅成组时不漏看），并覆盖“已登记选择但意图未登记”的组（如下载器后配置），不再只过滤“尚无选择记录”的候选。
- 意图登记：同一优胜且意图已有着落（已登记任务或已记录失败）时不再重试种子下载——此前每次提交选择计划都会重拉种子并清空失败记录，低位新候选触发的重算会自动重试；人工重评入口（`ReevaluateAsync`）以 `resetFailedIntent: true` 显式授权重试。
- 落选标记：未命中规则进入待确认的候选 `SelectionOutcome` 置 None，不标 RunnerUp（此前遍历整个 Ranking 会把它显示成“组内落选”）。
- 同组升级判断与规则匹配一致改用忽略大小写比较：现任 ANi、新候选 ani 视为同组修订升级，不再因大小写保留旧版本。
- 顺带修正 `DescribeUnmatched` 返回元组的可空标注（CS8619）。

## 锁定决策（无新证据不重新讨论）

- 同集分组键 = 修正后 SubjectId + 解析 MediaType + 修正后 Episode（decimal）。普通集与 OVA 等按类型分开分组；多集合集不参与自动择优。
- 修订版本比较仅在同字幕组（与规则匹配一致，不区分大小写）内生效；不同字幕组之间不凭版本号跨越规则优先级，也不凭版本号替换同优先级的现任优胜。
- 同优先级保留现任优胜（严格升级判定，不进入排序链）；无现任时按字幕组名、修订版本、发布时间、StableId 的基础全序决胜。
- 同 hash 任意状态（含 Failed/Replaced）任务存在即不重复登记新任务；失败任务等待人工重试，不自动重建。
- 种子获取失败如实落库失败原因，不自动反复重试。
- 默认规则（内置）：LoliHouse 先于 ANi，简日（JpSc）先于简中（Sc），先字幕组后语言，仅 1080p，未列出者兜底参与。具体排名由用户后续通过配置修改，计划不替用户指定。

## 测试

- `CandidateRankingPolicyTests`（16）+ `CandidateEligibilityPolicyTests`（4）：排列不变性、两种维度顺序、组合首匹配与通配兜底、完全 / 包含匹配、未知值与未命中策略、同优先级保留现任（含跨组不按字母序替换）、修订版本升级（含现任 v1 遇同组 v2 与他组候选同层的无循环回归、字幕组大小写变化仍算同组升级）、硬性过滤。
- `CandidateSelectionServiceTests`（15，SQLite）：同批正序 / 倒序同优胜且仅登记优胜、跨轮升级保留旧任务、同 hash 完成任务复用不重建、非优胜条目手动创建被拒、批次仍有待评估条目时恢复跳过（含已登记选择但意图未登记的组）、下载器未就绪换优胜后恢复补登记新优胜、新优胜种子失败后意图字段重置、同优胜重算不重试已失败种子与不重拉已登记种子（人工 resetFailedIntent 除外）、待确认候选不标组内落选、三种中断恢复场景。
- `SubscriptionRefreshTests`（10）与 `FeedItemDetailsPageViewModelTests`（4，含不再合格条目清除落选标记）保持通过；`DownloadServiceTests`（24）按新门槛补优胜标记后全部通过，断言语义未变。
- 全量：`dotnet build MediaManager.slnx` 0 警告 0 错误；`dotnet test` 118/118 通过（2026-09-27）。

### 迁移校验失败（P3 起遗留问题，已定位并修复）

- 两个使用 `MigrateAsync` 的测试曾以“偶发失败，重跑即过”记录；2026-09-26 午后演化为全量跑稳定失败、单类通过，错误为 EF `PendingModelChangesWarning`。
- 根因：**测试 Identity 配置不一致导致模型缓存顺序依赖**。`ApplicationDbContext` 继承 `IdentityDbContext`；订阅与设置测试注册了 `AddIdentityCore().AddEntityFrameworkStores()`，而下载、择优、条目详情测试没有。进程内哪个测试先构建模型决定了缓存内容：缺 Identity 注册的测试先构建时，后续 `MigrateAsync` 的模型校验与迁移快照（含 Identity 模型）比对出现差异。测试集合的加入与执行顺序解释了 P3 期间的“偶发”与 P4 期间的“必现”。与并行、编译产物、迁移内容均无关（实验排除）。
- 修复：三个测试的服务注册补齐与应用一致的 `AddIdentityCore().AddEntityFrameworkStores()`；移除此前的 `ConfigureWarnings` 警告抑制。全量 108/108 连续两轮通过。
- 附带发现并修复真实兼容缺陷：`FeedItem.SelectionOutcome` 迁移默认值曾被生成为空字符串，存量行升级后枚举字符串转换会解析失败；已改为显式 `.HasDefaultValue(SelectionOutcome.None)` 并重新生成迁移。

## 规则配置与比较预览（2026-09-27 实施）

P4 前置第 5 步剩余部分：规则从内置默认变为可编辑配置，保存前可预览影响。

### 配置存储与版本

- `SelectionRuleConfigSerialization`（Core）：整份配置以 JSON 序列化到 `ApplicationSetting` 单键 `Selection.RuleConfig`（非保护字段，不含敏感信息）；反序列化遇损坏内容、缺字段或未知枚举值时返回 null，读取方回退内置默认规则（记 Warning，不自动改写损坏数据）。
- `Normalize`：保存前字幕组名去首尾空白、剔除空项。
- `ContentEquals`：判断保存是否真的修改了规则——忽略版本号与字幕组大小写（与规则匹配语义一致），分辨率按集合比较（硬性过滤与顺序无关）。
- `ManageSelectionRuleSettings.SaveAsync`：规范化后与已存配置做内容比较；未变化则不写存储、版本保持；有变化则版本 = 现有 + 1 后整体落库。
- `SettingsSelectionRuleProvider` 替换 `DefaultSelectionRuleProvider`（该类仅因测试引用保留，不再注册）；`ISelectionRuleProvider` 改为 Scoped，与消费方 `FeedItemEvaluator`、`CandidateSelectionService` 一致。

### 规则变更重选与预览

- `CandidateSelectionService.ReselectAllGroupsAsync`：保存规则变化后对全部合格组（含已有选择记录的组）按新规则重算并落库；复用恢复扫描的组级 Pending 守卫（组内任一候选所属订阅仍有 Pending 条目即整组等待），自动路径语义（resetFailedIntent=false，同优胜失败意图不自动重试）。恢复扫描的对应查询逻辑提取为共享私有方法，行为不变（既有测试锁定）。
- `CandidateSelectionService.PreviewRuleChangeAsync`：保存前比较预览与执行走**同一条计算路径**（`PlanSingleGroupAsync`），对全部通过守卫的组用草拟规则重算优胜并与已保存选择比较；纯读操作，不落库、不下载种子、不创建任务。结果含组名（Subject 中文名优先）、当前优胜、草拟优胜、优胜依据与是否变化。
- 页面 `/selection-rules`（导航"择优规则"）：独立模式（字幕组/语言顺序增删排序、维度顺序）、组合模式（有序条件、通配）、语言匹配方式、未命中处理、允许分辨率（全不选=不限制）、版本徽章、比较预览表格（摘要 + 组级变化）与保存。语言下拉不提供 `None`（非可配置组合）。
- 条目详情页"同集选择"下展示所在组的规则版本与组内优胜依据（`FeedItemView.SelectionRuleVersion/SelectionBasis`，仅详情查询填充）。

### 测试

- `SelectionRuleConfigSerializationTests`（11）：往返、损坏/未知枚举/缺版本/空字幕组名拒绝、内容相等（版本/大小写/分辨率顺序）与不等（顺序交换/通配差异）、规范化。
- `ManageSelectionRuleSettingsTests`（5）：缺键默认、损坏回退不修复、首存版本 2 落库、三种未变化短路、空白规范化。
- `CandidateSelectionServiceTests` 追加（5）：预览与执行一致（含保存+重选闭环断言库中优胜与依据）、Pending 组排除预览、整库重选组数、同优胜失败意图不重试、Pending 组重选跳过。
- `SelectionRulesPageViewModelTests`（5）：加载草稿、保存有变化触发重选（RuleVersion==2）、未变化不重选、预览填充、空白字幕组名拒绝。
- 真实浏览器冒烟（隔离临时库实例）：页面渲染与默认值、预览（空库 0 组）、原样保存提示未变化、添加字幕组保存后版本 2 并触发重选、刷新后持久化。全量 144/144 通过（2026-09-27）。

## 一次性 RSS 订阅（2026-09-27 实施）

P4"来源与投递"第一项：临时 RSS 以一次性订阅形态接入，复用既有批次管线，不做定时调度。

### 语义与实现

- **不参与定时调度**：`MediaSubscription.IsOneShot`（默认 false，EF 迁移 AddOneShotSubscription）；调度器 `EnqueueDueRefreshesAsync` 按 `IsEnabled && !IsOneShot` 过滤；`CompleteAsync`/`FailAsync` 对一次性订阅不排 `NextRefreshAtUtc`（恒为 null）。一次性订阅保存时 `IsEnabled=true`，但"启用"只表示可手动刷新。
- **添加即抓取**：复用 `SaveAsync` 已有的"新建启用订阅后立即入队"路径；URL 放宽为任意 HTTPS（本机 HTTP 例外）——Mikan 域名白名单仅约束定时订阅（其条目链接解析依赖 Mikan 页面）；名称可空，留空派生"一次性 RSS：<Host>"。`IsOneShot` 仅创建时设定，编辑分支不修改。
- **立即唤醒**：新增 `RefreshPulse` 单例（粘性标记 + TCS），`RequestRefreshAsync` 入队后唤醒 `SubscriptionRefreshWorker` 立即处理，不等最长 15 秒的空闲轮询；手动刷新与一次性订阅添加同时受益。脉冲在有等待者之前发生时置位标记，等待方立即返回，不丢信号。
- **Subject 关联**：一次性 RSS 条目无 Mikan 链接，按既有优先级走"人工指定 → 标题映射"路径；标题命中已有映射即正常进入硬性过滤与择优管线（测试锁定：Mikan 订阅先建立映射，同标题一次性条目随后关联同 Subject 并入组择优）；未命中进入待确认，人工指定后重评自动建立映射。
- **页面**：订阅页新增"一次性 RSS 订阅"卡片（地址必填 + 名称可选）；列表一次性订阅加"一次性"徽标、刷新周期显示"—"；编辑一次性订阅时隐藏定时刷新字段；"立即刷新"按钮照常可用（重复抓取入口）。

### 测试

- 一次性语义（`SubscriptionRefreshTests` 4 个）：非 Mikan HTTPS 地址创建即入队且不入调度、名称派生；本机 HTTP 放行 / 非本机 HTTP 拒绝；完成后 `NextRefreshAtUtc` 保持 null；标题映射命中后进入择优管线（同组仅一个优胜）。
- `RefreshPulseTests`（4 个）：先脉冲后等待立即返回、等待中脉冲释放、无脉冲保持等待、信号消费后重新等待。
- `SubscriptionsPageViewModelTests`（3 个）：一次性草稿转请求、名称留空派生、非法地址错误回显不落库。
- 全量 155/155 通过（2026-09-27）。浏览器冒烟：订阅页渲染、一次性卡片与徽标正常；表单输入的端到端提交受 IAB 测试环境事件投递限制未能自动化验证（非应用缺陷，既有登录页同样受影响），该链路由 VM 与服务集成测试覆盖。

## 通知投递：Telegram Bot 与 Gotify（2026-09-27 实施）

P4"来源与投递"第二项：新增通知渠道 Telegram Bot 与 Gotify，遵循 plan.md"按渠道独立保存结果与重试"的设计约束。

### 语义与实现

- **事件范围**：本期实现两个事件——下载开始（`SubmitAsync` 中 `Submitting → Downloading` 确认点，含作品名、集数、版本与命名快照）与下载失败（`MarkFailedAsync` 汇聚点，含失败步骤与错误）。事件枚举可扩展（下载完成、新集登记等后续按需增加钩子）。恢复路径（响应丢失后按 hash 对账进入下载中）不补发开始通知。
- **同事务入队**：`NotificationEnqueuer.TryEnqueueAsync` 读取两个渠道设置，为每个"启用且订阅该事件"的渠道向调用方上下文追加 `NotificationDelivery` 行，与任务状态变更在同一次 `SaveChangesAsync` 落库（plan.md"投递意图与状态同事务写入"）；入队自身异常只记日志，绝不影响下载主流程（测试锁定：通知设置存储故障时任务照常提交）。
- **渠道独立投递**：`NotificationDeliveryService` 每 30 秒（`NotificationDeliveryWorker`，PeriodicTimer 模式）投递到期 Pending：成功 → Delivered + 送达时间；失败 → 尝试计数 +1、指数退避（1/2/4/…分钟封顶 30）；累计 10 次失败置 Failed 终态，投递记录页可人工重试（清零重新入队）。渠道未启用/未配置时跳过并保留 Pending，重新启用后自动续投；启用但 Token 缺失按失败如实记录（Last Error 提示配置项）。
- **Telegram Bot**：`TelegramSender` POST `https://api.telegram.org/bot{token}/sendMessage`（chat_id + text，UTF-8 原样编码）；`ok:false` 或非 2xx 均为失败并以 description 报错。代理支持 http/https/socks5（URL 可携带 user:pass），留空沿用系统代理、填写即覆盖；因代理按次可能不同，发送器按次创建 `SocketsHttpHandler`（通知低频，开销可忽略）——有意偏离命名 HttpClient 模式。Bot Token 加密存储（Data Protection），空白表示沿用原值。
- **Gotify**：`GotifySender` POST `{baseUrl}/message?token={appToken}`（title + message）；无优先级字段，客户端按应用 Token 分组显示（经确认不需要"频道"语义）；自托管直连不走代理。
- **设置与 UI**：设置页新增 Telegram / Gotify 两张卡片（启用开关、Token 密码框空白沿用、Chat ID / 地址、代理地址、事件复选框），各带"发送测试消息"按钮（用草稿设置真实发送，Token 留空回落已存值）。新页面 `/notifications`（导航"通知"）列出最近投递（渠道、事件、内容、状态、尝试次数、最后错误），Failed 记录提供"重试"。
- **重复通知边界**（plan.md 如实记录）：远端已接收而响应丢失时会重复投递，at-least-once 是设计边界而非缺陷。

### 测试

- 发送器桩测（`TelegramSenderTests` / `GotifySenderTests`，TCP 桩 `StubHttpServer` 与 qBittorrent 桩同一思路）：sendMessage 路径与正文、ok=false 与非 2xx 失败语义、代理经桩转发的绝对 URI 请求行、代理 scheme 校验与凭据剥离、Gotify token 端点与空 title 省略字段。
- 投递服务（`NotificationDeliveryServiceTests`）：双渠道独立成功/失败互不影响、退避到期前跳过到期后续投、重试用尽置 Failed、人工重试清零重投、渠道禁用保留 Pending 零消耗、Token 缺失失败提示、`NotificationEvents` 解析/格式化回归。
- 入队与钩子（`NotificationEnqueuerTests` / `NotificationHookTests`）：事件订阅过滤、超长内容截断、设置存储异常吞掉；提交成功/提交被拒/未配置/未订阅四种场景下的投递行断言，入队内容含作品名与命名快照。
- 设置（`ManageNotificationSettingsTests`）：Token 加密标记、空白沿用、启用前置校验（Token/Chat ID/地址/代理 scheme）、测试发送草稿优先与回落。
- 全量 219/219 通过（2026-09-27）。真实 Telegram / Gotify 端到端投递未验证（需真实凭据，按仓库惯例待用户授权单独执行）。

## 待完成

1. 真实 qBittorrent 端到端验证（P3 起遗留，含择优产生的升级任务、规则变更重选与一次性订阅产生的任务）。
2. P3 历史数据兼容验证：历史 Accepted 条目经人工重评触发重选；迁移副本与离线回放按 P4 验收标准执行。
3. 跨订阅批次仅保证共用候选与重复保护，不承诺跨源同步等待窗口（按计划设计边界）。
4. P4 下一增量：Bangumi.moe 来源接入与 Jellyfin 投递（通知投递已于 2026-09-27 实施，见上文）。
