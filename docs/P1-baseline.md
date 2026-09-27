# P1 工程基础验收

日期：2026-09-12

## 已完成

- 项目收敛为 Core、Application、Web 宿主和 Tests；外部集成与持久化实现在宿主内按目录组织，不设置 Infrastructure 项目。
- Web 使用 Blazor Interactive Server；首页、登录、导航和设置入口形成最薄管理界面，业务页面要求登录。
- Identity 与业务数据共用 SQLite `ApplicationDbContext`，启动时自动应用迁移；开放注册关闭，空库通过环境变量创建首位已确认管理员。
- SQLite 数据库和 Data Protection 密钥位于 `App_Data`，部署时需要共同持久化和备份。
- `IApplicationSettingStore` 提供普通值、受保护值和删除操作；受保护值以 Data Protection 密文写入数据库，读取时解密。
- 常规设置用例目前只管理实例名称。qBittorrent、通知、Jellyfin 等具体设置在对应集成阶段增加，不提前建立空配置模型。
- P1 不注册无业务行为的空 Worker。P2 的订阅刷新将成为第一条真实后台任务，宿主 Worker 调用有界的 Application 用例，不依赖 Blazor 会话。

## 数据库迁移

- `20260908073116_InitialSqlite`：Identity 表。
- `20260911151848_AddApplicationSettings`：应用设置表。

## 验证结果

- `dotnet build MediaManager.slnx --no-restore`：0 个警告，0 个错误。
- `dotnet test MediaManager.Tests/MediaManager.Tests.csproj --no-restore --no-build`：11 个测试全部通过。
- EF Core 模型与最新迁移一致，无待处理模型变更。
- 临时空数据目录启动验证已应用两条迁移、创建首位管理员并启动监听；未连接 RSS、Bangumi、qBittorrent、通知或 Jellyfin。
