<!-- markdownlint-disable -->

<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="Banori/wwwroot/images/banori-logo-white.png">
  <img alt="Banori" src="Banori/wwwroot/images/banori-logo.png" width="160" height="160">
</picture>

# Banori

<br>

<div>
    <a href="https://github.com/banned2054/Banori/releases/tag/v0.1.0"><img alt="Version 0.1.0" src="https://img.shields.io/badge/version-0.1.0-blue"></a>
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
ghcr.io/banned2054/banori:v0.1.0
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

### 使用 bind mount 时的目录权限

镜像默认以 root 启动入口脚本：将 `/app/data` 及其全部内容的属主修正为 `PUID`/`PGID`（默认 `1654:1654`，即 .NET 镜像惯例的非 root 身份）后，降权到该用户运行应用。在允许修改属主的普通 Linux 本地文件系统上，bind mount（如 `./data:/app/data`）即使指向 Docker daemon 自动创建的 `root:root` 目录，也无需在宿主机手工 `mkdir`/`chown`。

希望 `data/` 里的文件归属你的 NAS 账号（便于在文件管理器中查看备份）时，在 compose 的 `environment` 中设置 `PUID`/`PGID`，并移除服务上的 `user:` 配置。例如宿主机账号的 UID/GID 为 `1000:1000` 时：

```yaml
services:
  banori:
    image: ghcr.io/banned2054/banori:latest
    ports:
      - "7340:7340"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      PUID: "1000"   # 宿主机账号的 uid（id -u 查看）
      PGID: "1000"   # 宿主机账号的 gid（id -g 查看）
      Username: ${BANORI_USERNAME:-}
      Password: ${BANORI_PASSWORD:-}
    volumes:
      - ./data:/app/data
```

**自动属主修正在每次启动时递归执行，不仅针对新目录。** 只应挂载 Banori 专用数据目录，不要挂载其他服务共用的目录；未设置 `PUID`/`PGID` 时，已有文件的属主也会被改成 `1654:1654`。NFS root-squash、部分 NAS ACL 或不支持修改属主的文件系统可能使 `chown` 失败，入口脚本会停止启动；这类存储应先在宿主机配置访问权限，再用非零 `user:` 跳过自动修正。

rootless Docker 下容器内的 UID 0 映射到宿主机当前用户。移除 `user:`，设置 `PUID: "0"` 与 `PGID: "0"`，应用将以容器内 UID/GID 0 运行，而不是宿主机 root。仅设置 `user: "0:0"` 不够：入口脚本仍会按默认 `PUID`/`PGID` 降权到 `1654:1654`。

也可以保留 `user: "1000:1000"` 这类非零 UID 配置：入口脚本会跳过属主修正和身份切换，`PUID`/`PGID` 不生效。此时必须自行确保宿主机目录及已有文件允许该身份读写；`user:` 本身不会改变 Docker 自动创建目录的属主。权限不足时应用启动即失败，日志会打印当前进程 uid/gid 与修复建议。

> 下载整理还需要 qBittorrent。它可以运行在另一容器或主机上；在「设置」中填写 qBittorrent 可访问的下载路径，并确保 Banori 与 qBittorrent 对该路径的挂载映射一致。仅让 Banori 容器连接到 qBittorrent 并不足以让它访问下载文件。

### 日志与诊断

默认保留 `Information` 及以上的业务日志；EF Core 的 `Microsoft.EntityFrameworkCore.Database.Command` 类别只输出 `Warning` 及以上日志，不输出每条执行成功的 SQL。该默认设置同时适用于生产和开发环境。

环境变量会覆盖 `appsettings.json` 和 `appsettings.{Environment}.json` 的同名配置，层级分隔符 `:` 用双下划线 `__` 表示。Docker Compose 可在原有 `environment` 中追加：

```yaml
    environment:
      Logging__LogLevel__Banori: "Debug"
      # 仅在需要排查数据库时启用 SQL 输出：
      # Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command: "Information"
```

`Logging__LogLevel__Banori` 只调整 Banori 应用类别的门槛，不会开启 EF Core 的成功 SQL 日志。日志等级是过滤门槛，不会改变事件本身的等级；设为 `Debug` 也不会自动生成尚未实现的阶段日志。若需要调整全局门槛，可使用 `Logging__LogLevel__Default`，但更具体的类别设置仍优先，因此设全局为 `Debug` 也不会覆盖 SQL 类别的 `Warning`。

修改 Compose 环境变量后执行 `docker compose up -d`，让 Compose 重建配置发生变化的容器；仅执行 `docker compose restart` 不会应用新的环境变量。使用 `docker compose logs -f banori` 查看日志。排查结束后移除临时覆盖并再次执行 `docker compose up -d`。

本地 PowerShell 示例：

```powershell
$env:Logging__LogLevel__Banori = "Debug"
dotnet run --project Banori/Banori.csproj
```

SQL 日志可能包含业务数据或 SQL 文本中的字面量，不建议长期启用或未经检查直接分享；上述配置不会启用 EF Core 的敏感参数值日志。

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
