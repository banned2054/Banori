# qBittorrent 4.6.4 容器实测记录（隔离验收）

实测环境：linuxserver/qbittorrent:4.6.4（4.6.4-r1-ls332），Docker Desktop / Windows 宿主，
容器 `banori-accept-qb-smoke`，WebUI `http://127.0.0.1:18081`（只绑回环），BT 端口 16881。
实测时间：2026-11-14。所有结论以本次实测为准；announce 全部用 `http://tracker.invalid/announce`
（dummy，qB 解析失败即放弃，无真实 tracker 流量）。

## 版本与 API

| 端点 | 实测返回 |
| --- | --- |
| `POST /api/v2/auth/login`（成功） | **HTTP 200** + body `Ok.` + `Set-Cookie: SID=...`（**不是 204**；失败 = 200 + `Fails.`，封禁后 403） |
| `GET /api/v2/app/version` | `v4.6.4` |
| `GET /api/v2/app/webapiVersion` | `2.9.3` |

## 关键差异（与代码原假设不符，已按实测修正）

1. **登录成功码是 200 不是 204**。`wait_webui_ready` / 注释里的「204 + SID」是 5.x 语义；
   4.6.4 返回 200 + `Ok.`。
2. **`torrents/add` 的停止态参数是 `paused=true`**（WebAPI 2.9.x）。发 `stopped=true`
   被**静默忽略**——种子直接以运行态加入（实测 `stalledDL`）。5.x 才改名 `stopped`。
   兼容两代：两个参数都发。
3. **conf 凭据必须在 `[Preferences]` 节且带 `WebUI\` 前缀**（`WebUI\Username`、
   `WebUI\Password_PBKDF2`）。写进独立 `[WebUI]` 节、无前缀的裸键 qB **不认**，
   会生成临时密码（首启日志打印），预置凭据全部失效。PBKDF2 格式
   `b64(salt):b64(key)`（SHA512、100000 迭代、64 字节）外层 `@ByteArray(...)` 实测有效。
4. **Host 头端口校验**：`HostHeaderValidation=false` 关不掉。宿主 18081 → 容器 8081
   的映射一律 401「Invalid Host header, port mismatch」（上次日志 119 条）。修法：
   容器内外同端口（`WEBUI_PORT=18081` + `-p 127.0.0.1:18081:18081`），conf
   `WebUI\Port=18081`。
5. **单文件种子布局**：qB 按 `info.name` 落数据（数据文件 = `<savePath>/<info.name>`）。
   make_torrent.py 旧版写非标准 `info["path"]` 键被 qB 忽略 → recheck 永远 0。
   修正后单文件 info 只有 `length`（无 path 键），`name` = 内容文件名。

## state 串实测（4.6.4；5.x 括号内为对应命名差异）

| 操作 | 实测 state |
| --- | --- |
| 停止态添加（`paused=true`） | `pausedDL`（5.x `stoppedDL`） |
| 运行态添加/无数据 | `stalledDL` |
| recheck 进行中（400KB 秒级，采样可见） | `checkingDL` |
| recheck 完成（停止态） | `pausedUP` progress=1 amount_left=0（5.x `stoppedUP`） |
| pause（完成态） | `pausedUP` |
| 普通 `torrents/resume` | `stalledUP`（`force_start=false`；`resume?force=true` **不是**强制开始，实测仍 stalledUP） |
| `torrents/setForceStart value=true` | `forcedUP` + `force_start=true`（拼写就是 forcedUP，非 forceUP） |
| 删除（deleteFiles=true） | 生效，列表消失且文件被删 |

- pieces_have/pieceStates：recheck 前 `[0,0]` → 完成后全 1（400KB / 256KB piece = 2 块）。
- tracker 状态：dummy announce（tracker.invalid）→ `msg: "Host not found (authoritative)"`，
  status=0，不影响做种；DHT/PeX/LSD 条目 status=2（working）但均无 peer。

## savepath 行为

- `savepath=/downloads/probe-in`（容器内子目录）正常生效，`torrents/info` 的
  `save_path` 原样返回 `/downloads/probe-in`；volume 挂载（宿主 `.e2e/smoke/downloads`
  → 容器 `/downloads`）下预放文件对 qB 可见。
- 单文件种子添加到 `savepath=/downloads` 时数据文件直接平铺为 `/downloads/<info.name>`，
  **没有** `<name>/` 子目录（多文件种子才有根目录层）。

## 出网行为（隔离性评估）

- 容器不需要外网。实测有 GeoDB 离线包加载（首启一次性下载 dbip-country-lite.mmdb，
  config 卷已缓存）、UPnP 尝试（no router found，失败即放弃）、外网 IP 探测
  （「Detected external IP」）——均为一次性/无害，未发现持续性不可控出网。
- dummy tracker 域名解析失败后 qB 不重试轰炸，行为可控。

## 对测试代码的落地修正

- `qb_container.py`：write_conf 凭据/端口写法（见上）、登录判定 200+`Ok.`+SID、
  端口空闲预检、宿主 qB 冲突感知（只警告，不阻止）。
- `QbApiClient.AddStoppedAsync`：同时发 `paused=true` 与 `stopped=true`。
- `QbApiClient.ForceResumeAsync`：改走 `/api/v2/torrents/setForceStart`（value=true）。
- `QbApiClient` 新增 `GetTorrentFilesAsync`（`torrents/files`）、`SetLocationAsync`
  （`torrents/setLocation`）、`ResumeAsync`。
- `ScenarioPrep`：单文件种子目标路径平铺（无 `<name>/` 层）；完成判定加 `AmountLeft==0`。
- `make_torrent.py`：单文件种子去掉非标准 `info["path"]`。

## 冒烟实测结论（2026-11-14）

- `E2E_SMOKE=1` 冒烟两次通过（各约 18s）：Banori 任务到 Downloading（status=2）、
  SiteSource=byrbt、SubjectId=114514（标题映射命中）；qB 侧种子 `pausedDL` + tag
  `byrbt`（Banori 以停止态提交，4.6.4 的 pausedDL 即 5.x stoppedDL）。
- **重跑注意**：冒烟不清理 qB 侧种子（设计如此，留给外部清理）。连续重跑前必须先删
  qB 里同 infohash 的残留种子——实测残留场景下（qB 已有同 hash 停止态种子，Banori DB
  全新）对账会按「外部停止且系统未声明准备」把任务保守判为 Paused（人工暂停语义，
  DownloadService.cs:3431 分支），冒烟等不到 Submitting/Downloading 而超时。此为生产
  代码既有行为记录，未改动生产代码。
