<!-- markdownlint-disable -->

<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="Banori/wwwroot/images/banori-logo-white.png">
  <img alt="Banori" src="Banori/wwwroot/images/banori-logo.png" width="160" height="160">
</picture>

# Banori

<br>

<div>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="https://learn.microsoft.com/aspnet/core/blazor/"><img alt="Blazor" src="https://img.shields.io/badge/Blazor-Interactive_Server-7B2CBF"></a>
    <a href="https://learn.microsoft.com/ef/core/"><img alt="EF Core" src="https://img.shields.io/badge/EF_Core-10-2EA44F"></a>
</div>

<br>

<!-- markdownlint-restore -->

</div>

**Banori**（番組 × 織り）是面向个人或家庭媒体库的动漫 RSS 管理面板，基于 .NET 10 与 Blazor 构建。它可以管理 Mikan / AniBT 订阅、分析并关联 Bangumi 条目，评估优胜条目并登记下载任务，再提交到 qBittorrent，完成后按模板整理文件。

## 🚧 项目状态

核心链路「订阅 → 评估 → 下载 → 改名」可供日常使用；界面与配置仍会随需求演进。

## ✨ 功能特性

- **订阅与刷新** — 管理 Mikan、AniBT RSS，定时刷新并去重入库；RSS 地址加密保存。
- **条目评估** — 解析条目信息、关联 Bangumi，并展示筛选理由与命名预览。
- **下载管理** — 将条目提交到 qBittorrent，跟踪任务状态；支持 RSS 链接和本地种子。
- **整理归档** — 下载完成后按命名模板整理媒体文件，并将附属文件移入 `Seed/`。
- **数据库** — 默认使用 SQLite，也可配置 MySQL。

## 🐳 Docker 部署

需要安装 Docker Engine 和 Docker Compose。容器将 Web 服务监听在 `7340` 端口，并通过 Docker 命名卷 `banori-data` 持久化数据库与加密密钥。

```bash
cp .env.example .env
```

编辑 `.env`，为首次启动设置管理员用户名和强密码：

```dotenv
BANORI_USERNAME=admin
BANORI_PASSWORD=替换为强且唯一的密码
```

启动并查看日志：

```bash
docker compose up -d
docker compose logs -f banori
```

浏览器访问 <http://localhost:7340>。首位管理员创建后，可从 `.env` 删除 `BANORI_PASSWORD`（也可移除 `BANORI_USERNAME`），然后执行 `docker compose up -d` 使环境变量更新。请勿公开 `.env` 或将其提交到 Git。

停止服务但保留数据：

```bash
docker compose down
```

升级到最新镜像：

```bash
docker compose pull
docker compose up -d
```

GitHub Actions 会在 `master` 更新时发布 `latest`，推送 `v*` 标签时发布同名版本镜像，例如：

```text
ghcr.io/banned2054/banori:latest
ghcr.io/banned2054/banori:v1.0.0
```

### 数据持久化与迁移

Compose 使用 Docker 命名卷 `banori-data` 挂载到容器 `/app/data`。SQLite 数据库位于 `data/banori.db`，Data Protection 密钥位于 `data/keys/`；两者都必须保留。删除容器或执行 `docker compose down` 不会删除该卷；**不要使用 `docker compose down -v`**，除非确定要删除全部 Banori 数据。

可用下列命令备份命名卷（先停止服务，以保证 SQLite 文件一致）：

```bash
docker compose down
docker run --rm \
  -v banori-data:/source:ro \
  -v "$PWD/backups:/backup" \
  alpine:3.20 \
  tar -czf /backup/banori-data.tar.gz -C /source .
docker compose up -d
```

恢复时先停止服务，将备份解压到同一个命名卷，再启动应用。恢复数据库时必须同时恢复 `keys/`，否则已保存的 RSS 地址和其他加密配置可能无法解密。

若从旧版或本地运行迁移，先停止旧应用，将原 `Banori/App_Data/` 的内容（数据库文件以及存在时的 `keys/`）复制到 Docker 卷根目录，再启动容器。确保文件权限允许容器进程读写。迁移前请另做备份；不要让旧版和新版同时写同一数据库。

> 下载整理还需要 qBittorrent。它可以运行在另一容器或主机上；在「设置」中填写 qBittorrent 可访问的下载路径，并确保 Banori 与 qBittorrent 对该路径的挂载映射一致。仅让 Banori 容器连接到 qBittorrent 并不足以让它访问下载文件。

## 🛠️ 本地开发

需要安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。使用空数据库启动时，先通过环境变量设置首位管理员：

```powershell
$env:Username = "admin"
$env:Password = "请替换为强密码"
dotnet run --project Banori/Banori.csproj
```

应用配置也可写入 `Banori/appsettings.json`，或通过环境变量覆盖。分层配置使用双下划线，例如：

| 环境变量 | 用途 |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | 数据库连接字符串 |
| `Database__Provider` | 数据库类型：`Sqlite` 或 `MySql` |
| `Database__MySqlServerVersion` | MySQL / MariaDB 服务器版本；填写后可跳过启动探测 |

默认 SQLite 数据库为 `Banori/data/banori.db`。MySQL 与 SQLite 共用同一 EF Core 模型，迁移集按数据库类型隔离，分别位于 `Banori/Data/Migrations/` 与 `Banori/Data/Migrations/MySql/`。

## 🧪 验证

```powershell
dotnet build Banori.slnx
dotnet test Banori.Tests/Banori.Tests.csproj
```
