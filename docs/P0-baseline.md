# P0 行为基线与决策

日期：2026-09-08

## 已采用的基础决策

- 首版采用单实例 ASP.NET Core / Blazor Interactive Server，面向 Linux 或 NAS 容器部署与 Windows 开发。
- 首版数据库采用 SQLite，数据库文件位于应用持久化目录，不放置在网络共享目录。
- 首版只自动处理单视频文件；多集或多文件合集进入人工处理。
- 前端不保留 WebAssembly Client 项目。后台任务只依赖应用用例和持久化状态。
- 历史迁移范围留到 P5，根据真实导出数据决定，不影响 P1-P4 的新模型。

## SDK 基线

### Banned.AniParser 0.6.0

- 目标框架：net8.0、net9.0、net10.0。
- 使用入口：`AniParser.Parse(string)`；未匹配返回 `null`。
- 映射字段：标题与多语言标题、媒体类型、单集或范围集数、修订版本、组及组类型、语言、字幕类型、分辨率、来源、Web 来源、音视频编码和色深。
- MediaManager 将 Banned.AniParser 作为稳定的基础依赖，直接使用 `ParseResult`、`LocalizedTitle` 和相关枚举，不建立镜像解析模型或转发接口。持久化层只保存业务查询、恢复和审计实际需要的字段。
- 不假设 SDK 提供置信度。解析失败、多集与不可靠格式进入人工处理。

### Banned.Qbittorrent 1.6.0

- 目标框架：net8.0、net9.0、net10.0。
- `QBittorrentClient.Create` 完成登录和版本协商；支持请求超时、连接超时、重试次数、诊断、地址族策略与取消令牌。
- 已核对 P3 所需公开操作：添加与查询种子、查询文件、暂停、恢复、重新校验、删除、设置文件优先级、修改种子名、修改文件或目录名。
- SDK 默认进行网络重试。涉及添加种子的结果不确定场景必须先按 Hash 对账，不能把超时直接视为未添加。
- MediaManager 仅在 P3 通过应用层薄接口使用 SDK；P0/P1 不连接真实下载器。

### Banned.Bangumi 1.1.0

- 目标框架：net8.0、net9.0、net10.0。
- 使用 `BangumiClient.Subjects.Get` 和 `Search` 获取 Subject 详情与搜索结果；所有网络调用支持取消令牌。
- SDK 要求明确的 User-Agent，并提供超时、代理、访问令牌和调用方持有的 `HttpClient` 配置。
- MediaManager 不自行拼接或调用 Bangumi HTTP API。P2 通过应用层元数据接口和宿主 `Integrations` 目录中的实现使用该 SDK，并缓存映射结果；不为集成实现单设 Infrastructure 项目。

## 旧版规则基线

- 仅接受 1080p。
- 接受 ANi，并标记为 Baha；同集已有选择时不再采用 ANi。
- 接受 LoliHouse。
- 其他组仅接受 `JpSc` 或 `Sc`。
- Subject 420628 的集数大于 24 时映射到 486347。
- Subject 467461 的集数大于 12 时映射到 529431。
- Subject 484623 的集数大于 13 时集数减 13。
- 原有替换流程会先结束并删除旧任务文件；新版明确改变为先验证新资源，旧文件默认保留。
- 原有多集条目取起始集继续下载；新版明确改变为进入人工处理。

这些规则已经转成 Core 中可独立测试的默认策略。P2 的预览与后续实际执行必须调用同一策略，并保存决定理由。
