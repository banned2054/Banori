# .e2e 端到端验收（隔离 qB 4.6.4 容器 + 真机 Banori host）

四个独立验收：冒烟（SeedPipelineSmokeTests）、跨站搬运复用场景1（CrossSeedScenario1Tests）、
场景2（CrossSeedScenario2Tests）、元数据恢复（MetadataRecoveryScenarioTests）。
使用 Xunit.SkippableFact 的动态跳过：对应环境变量未设置时显示 [SKIP]
（只检查环境变量和操作系统，不访问文件系统、不启动 python/Docker），不影响常规 `dotnet test`。
每个场景自动管理自己的 qB 容器（测试内 up/down，串行绝不并行），
场景证据落盘 `.e2e/s1`、`.e2e/s2`、`.e2e/meta`（目录不存在时自动创建；本地保留不删，重跑覆盖）。
另有 `E2ePathResolutionTests` 的 3 个目录定位 [Fact]：无 gate、始终运行、自清理，
用于在干净源码布局下验证根定位与场景目录自动创建。

## 目录内容：源码 vs 本地生成物

- 进 Git 的源码/文档：`Banori.E2E.Tests/`（测试项目）、`tools/`（python 工具）、本 README、
  `smoke/qb-notes.md`（人工实测记录，下文关键实测结论的出处）。
- 只存在本地的生成物（根 `.gitignore` 已忽略，不提交）：`s1/`、`s2/`、`smoke-tmp/` 整目录，
  以及 `smoke/` 中除 `qb-notes.md` 外的运行产物（config、content、downloads、证据 JSON/torrent 等）。
- 场景目录（`.e2e/s1`、`.e2e/s2`、`.e2e/smoke`）由测试在对应 gate 启用后自动创建
  （`ScenarioSupport.FindSceneRoot` / `FindSmokeRoot`），无需提交目录或占位文件；
  生成物留在本地、重跑覆盖、不进 Git。
- `.e2e` 整体已从 Docker 构建上下文排除（根 `.dockerignore`），镜像构建不携带任何 E2E 内容。

## 前置

- Windows + Docker Desktop（linuxserver/qbittorrent:4.6.4，首次会 pull）。
- python 是 E2E 工具链（容器管理、种子生成、行为探针）的运行前提
  （默认 `%USERPROFILE%\.local\bin\python.exe`，可用 `E2E_PYTHON` 覆盖）；工具一律 `python -I` 调用。
- 隔离：WebUI/BT 端口只绑 127.0.0.1；announce 用真实 PT 域名（站点分类要求），
  但容器经 `--add-host tracker.byr.pt:127.0.0.1` 把该域名解析到自身回环（零真实 PT 流量），
  且 conf 关闭 DHT/PEX/LSD。announce 的 passkey 是 dummy。

## 运行方式

> 注：`--project` 形式在本机 .NET SDK 11.0.100 下报 MSB1001，统一改用位置参数
> `dotnet test .e2e/Banori.E2E.Tests`（项目路径作为第一个参数）。

```bash
# 冒烟（外部起容器方式，见 smoke/）
set E2E_SMOKE=1 && dotnet test .e2e/Banori.E2E.Tests --filter "FullyQualifiedName~SeedPipelineSmokeTests"

# 场景1：非受管候选（外部种子占位）复用全链；容器/端口/密码由测试自管（18085/16885）
set E2E_S1=1 && dotnet test .e2e/Banori.E2E.Tests --no-build --filter "FullyQualifiedName~CrossSeedScenario1" > .e2e/s1/test-output.txt 2>&1

# 场景2：正常完整链 + v2 受管候选映射修正（18086/16886）；两场景串行跑，绝不并行
set E2E_S2=1 && dotnet test .e2e/Banori.E2E.Tests --no-build --filter "FullyQualifiedName~CrossSeedScenario2" > .e2e/s2/test-output.txt 2>&1

# 元数据恢复：旧任务缺失 TorrentFile 元数据经 qB /torrents/export 补存（18087/16887）
# 重定向需目录预先存在：FindSceneRoot 在 gate 内才自动创建，干净检出没有 .e2e/meta/。
mkdir -p .e2e/meta
set E2E_META=1 && dotnet test .e2e/Banori.E2E.Tests --no-build --filter "FullyQualifiedName~MetadataRecovery" > .e2e/meta/test-output.txt 2>&1
```

（Git Bash 下用 `E2E_S1=1 dotnet test …` 形式。）测试结束自动 `qb_container.py down --remove`；
如需手动清理：`python -I tools/qb_container.py down --state s1/qb-state.json --remove`。

## 场景证据文件（每场景；本地生成物，不进 Git）

| 文件 | 内容 |
| --- | --- |
| `torrent-manifest-a/b(/c).json` | info-hash、piece-length、announce、文件 sha256/size |
| `evidence.jsonl` | EvidenceObserver 全程 2s 采样（任务全字段 + qB 种子快照） |
| `final.jsonl` | 终态快照：Banori 字段全集 + qB info+files + 宿主文件 sha256 复核值 |
| `summary.json` | 两个 info-hash、下载量基线/终值/增量、EpisodeVersions 终态、采样数 |
| `test-output.txt` | dotnet test 控制台输出 |
| `content-a/b(/c)/`、`torrent-*.torrent` | 种子生成输入与产物（复跑输入） |

## 工具

- `tools/qb_container.py`：隔离 qB 容器 up/down/status（凭据预写、同端口映射、
  `--add-host` 阻断真实 PT、DHT/PEX/LSD 关闭、端口占用只认 LISTENING/UDP）。
- `tools/make_torrent.py`：v1 单文件/多文件种子（真实 piece SHA1），输出 manifest JSON。
- `tools/probe_rename.py`：一次性行为探针（2026-11-14 实测：qB 4.6.4 无预分配；
  改名 onto 已占用文件为纯元数据变更且目标内容完好，recheck 复检可确认完整；
  stopped 添加已有数据后直接 resume 会自动校验到做种态）。保留供复查，不属常驻工具链。

## 关键实测结论（qB 4.6.4，详见 smoke/qb-notes.md 与场景证据）

- 停止态添加（paused=true）后种子有数据时：`resume` 触发 qB 自动校验（无需手工 recheck）。
- 复用校验的受控映射（无数据种子把文件改名到被占用目标）在 qB 侧可靠成立，目标文件不损。
- 复检秒级完成的种子，`Requested/ReadyToRun` 等意图子步骤在单个对账轮内落库并清除，
  2s 采样只见 Mapping→Observing；「本轮请求确已发出」由意图请求台账
  （RecheckRequestedAtUtc + RequestState=Sent）证明。
