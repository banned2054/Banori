# qBittorrent 文件 index 核实记录

核实日期：2026-10-16。

## 结论

- 同一个种子的文件改名、目录改名不会改变 Web API 的文件 index；实测重启后也保持不变。
- qBittorrent 4.4+ 会过滤 padding 文件，再按原始文件顺序连续编号。空文件仍占一个编号。
- 因此，Web API index **不等于包含 padding 的原始 `info.files` 列表下标**，也不能直接使用可能重排空文件的 MonoTorrent 列表下标。
- Banori 从原始种子识别文件名、集数及模板要素，按上述映射关联 qB 返回的当前路径。

## 源码核实

使用本地 qBittorrent 仓库的 Git 引用读取源码，无需切换工作区分支。

| Git 引用 | 核实结果 |
| --- | --- |
| `release-4.3.0` | 文件 API 尚未显式返回 `index`。 |
| `origin/v4_3_x`（`01519b5e7`） | API 显式返回 index；底层直接使用原生文件编号，尚未应用 4.4+ 的 padding 过滤映射。 |
| `release-4.4.0`、`origin/v4_4_x`、`origin/v4_5_x`、`origin/v4_6_x` | `TorrentInfo` 过滤 padding 后构建 `m_nativeIndexes`；API 返回这个映射中的连续位置。 |
| `origin/v5_0_x`、`origin/v5_1_x`、`origin/v5_2_x`、`origin/v5_3_x`、`master`（`df402f91d`） | 保持上述过滤与编号方式；改名通过 `nativeIndexes[index]` 定位原生文件。 |

关键实现：

- `src/base/bittorrent/torrentinfo.cpp`：构造 `m_nativeIndexes` 时检查 `!fileStorage.pad_file_at(nativeIndex)`。
- `src/webui/api/torrentscontroller.cpp`：`getFiles()` 遍历 `0 .. filesCount - 1`，把遍历位置作为 JSON 的 `index`，通过该位置查询当前路径。
- `src/base/bittorrent/torrentimpl.cpp`：`doRenameFile()` 使用 `nativeIndexes[index]` 调用 libtorrent；`handleFileRenamed()` 更新该位置的路径，不重排文件列表。

相关历史提交：

- `6f31ebd89`（2021-04-17）：Allow to specify file indexes in torrents/files API。
- `bc71827c0`（2021-10-02）：Improve torrent content handling，引入 padding 过滤映射；`release-4.4.0` 已包含。

4.3 系列与更早版本的行为不能直接套用 4.4+ 的编号规则。本次没有用旧版可执行文件进行运行验证。

## 真实 API 实测

环境：Windows，qBittorrent **5.2.4**，libtorrent **1.2.20.0**，Qt **6.10.3**。

使用独立临时 profile 和独立回环 Web API 端口；所有种子及内容均为本地生成的微型测试数据。

测试三种布局：

1. 单文件。
2. 多文件，包含同大小的两个视频及一个零字节文件。
3. 多文件，在第一个视频和空文件之间插入带 `attr=p` 的 padding。

对每种布局依次执行：添加种子 → 查询文件列表 → 逐文件改名 → 查询 → 目录改名 → 查询 → 关闭并重启测试实例 → 查询。

第三种布局的实际结果：

| 原始 `info.files` 下标 | 原始文件 | Web API index | 改名并重启后的 API 路径 |
| --- | --- | --- | --- |
| 0 | `Original/A.mkv`（1 字节） | 0 | `AnotherFolder/E00.mkv` |
| 1 | `Original/.pad/16383`（padding） | 不返回 | 不返回 |
| 2 | `Original/empty.txt`（0 字节） | 1 | `AnotherFolder/E01.txt` |
| 3 | `Original/B.mkv`（1 字节） | 2 | `AnotherFolder/E02.mkv` |

三种布局的文件改名、目录改名及重启检查全部通过。其他 qBittorrent 版本和 libtorrent 2.x 构建仅核对了相关 qB 源码，未进行同等运行测试。
