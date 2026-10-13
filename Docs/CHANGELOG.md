# Changelog

## 📘 Versions

- [v0.1.0](#-release-v010)

## 🚀 Release v0.1.0

[English](#v010-english) | [简体中文](#v010-zh-cn)

<a id="v010-english"></a>
### English

This is the first release of Banori, an anime RSS management panel for personal or family media libraries, built on .NET 10 and Blazor. The core pipeline — subscribe, evaluate, download, organize — is usable day to day; the interface and configuration may continue to change.

#### ✨ Added

- Anime RSS subscription management for Mikan and AniBT with scheduled refresh, deduplicated ingestion, and encrypted storage of feed URLs.
- Entry evaluation that parses torrent metadata, associates Bangumi subjects, picks the best candidate per episode, and shows filter reasons and naming previews.
- Download management that submits RSS links or locally uploaded torrents to qBittorrent, tracks task state, takes over existing qBittorrent torrents, and reconciles state instantly via a qBittorrent webhook with a polling fallback.
- Post-download organizing with separate torrent-name and file-name templates and source-explicit naming variables; companion files are moved into `Seed/`, and files replaced by a newer release are backed up into a `.replaced` directory instead of being deleted.
- An anime overview page that groups by download task, shows only the current version per episode, and provides a drill-down panel.
- Notification delivery through Telegram and Gotify with same-transaction enqueueing, per-channel retry, and a delivery history page.
- Named network proxies managed in Settings and applied across subscriptions, qBittorrent, Telegram, and Gotify.
- First-admin bootstrap via environment variables, with username login on the sign-in page.
- SQLite as the default database with optional MySQL support on a shared EF Core model.
- Docker deployment with a persistent data volume; images are published to GHCR.

#### ⚠️ Platform and limitations

- The release publishes container images to GHCR (`latest` from `master`, version images from `v*` tags); there is no installer, standalone binary, or automatic update support.
- Download organizing requires a qBittorrent instance, and Banori and qBittorrent must mount the download path consistently.
- The web UI is Chinese only in this release.
- The project is in early development; the interface, configuration, and data layout may change between releases.
- Migrating data from earlier local runs is a manual copy of `App_Data` into the Docker volume; see the README for the procedure and backup guidance.

<a id="v010-zh-cn"></a>
### 简体中文

这是 Banori 的首个版本，一个面向个人或家庭媒体库的动漫 RSS 管理面板，基于 .NET 10 与 Blazor 构建。「订阅 → 评估 → 下载 → 整理」核心链路可供日常使用；界面与配置仍会随需求演进。

#### ✨ 新增与改进

- 管理 Mikan、AniBT 动漫 RSS 订阅：定时刷新、去重入库，RSS 地址加密保存。
- 条目评估：解析条目信息、关联 Bangumi 条目、同集择优，并展示筛选理由与命名预览。
- 下载管理：将 RSS 链接或本地上传的种子提交到 qBittorrent，跟踪任务状态，接管 qBittorrent 存量种子；支持 qBittorrent 钩子即时对账，轮询兜底。
- 下载完成后的整理归档：种子名与文件名模板分离，并提供来源明确的命名变量；附属文件移入 `Seed/`，被新版本顶替的文件进入 `.replaced` 备份目录而非直接删除。
- 动画总览页按下载任务聚合，每集只显示现任版本，并提供单集下探面板。
- 通知投递：支持 Telegram 与 Gotify 渠道，同事务入队、渠道独立重试，并提供投递记录页。
- 设置页统一管理命名代理，覆盖订阅、qBittorrent、Telegram 与 Gotify 全链路。
- 通过环境变量引导首位管理员，登录页支持用户名登录。
- 数据库默认 SQLite，可选 MySQL，共用同一 EF Core 模型。
- 提供 Docker 部署与持久化数据卷，镜像发布到 GHCR。

#### ⚠️ 平台与已知限制

- 发布形态为 GHCR 容器镜像（`master` 更新发布 `latest`，`v*` 标签发布同名版本镜像）；不提供安装器、独立二进制或自动更新。
- 下载整理需要 qBittorrent，且 Banori 与 qBittorrent 必须以一致的方式挂载下载路径。
- 本版本界面仅提供简体中文。
- 项目仍处于早期开发阶段，界面、配置方式和数据布局在版本间可能变化。
- 从早期本地运行迁移数据需要手动将 `App_Data` 复制进 Docker 卷，操作步骤与备份说明见 README。
