# -*- coding: utf-8 -*-
"""生成真实可用的 v1 .torrent（Python 标准库 bencode，零第三方依赖）。

用法（-I 运行，隔离 sys.path，避免当前目录/脚本目录被植入模块干扰）：
    python -I make_torrent.py \
        --content-dir <内容目录> \
        --output <输出.torrent 路径> \
        --announce https://tracker.byr.pt/announce.php \
        --name "泣く大人は嫌い" \
        [--map 原相对路径=新文件名（可重复）] \
        [--piece-length 262144]

规则：
- content-dir 下的文件按相对路径字典序展开（含子目录）；多文件时 info 为 files 列表
  （每个文件 path=[各路径分量]，torrent name 为根目录名，--name 可覆盖）；单文件时
  info 为 length（无 path 键、无 files 列表），文件名 = info.name = 内容文件名——
  这是 v1 单文件种子的标准布局（qBittorrent/MonoTorrent 一致，实测 qB 4.6.4 按
  info.name 落数据，非标准的 info["path"] 会被忽略导致 recheck 找不到数据）。
- --map 做「内容文件相对路径 → torrent 内相对路径」的整体重映射（键为 content-dir
  内的相对路径，值为 torrent 内相对路径，均可含 / 分量）。构建番剧风格种子时用它把
  内容根下的通用名映射成「[字幕组] 标题 - 01 [1080p].mkv」等文件名。
- name 默认取 content-dir 目录名（仅多文件种子；单文件种子的 name 即文件名，--name 不适用）。
- piece 按 piece-length 分块，对全文件拼接流逐块 SHA1（真实哈希，qB recheck 可校验通过）。

输出 JSON（stdout 最后一行，UTF-8）：
    {"info_hash": "<40 hex>", "name": ..., "total_size": N, "piece_count": N,
     "piece_length": N, "files": [{"path": "<torrent 内相对路径>",
                                   "sha256": "<64 hex>", "size": N}, ...]}

info_hash 按 info 字典的 bencode 字节计算，键按字节序排序，与 MonoTorrent/
qBittorrent 的 v1 哈希一致，可直接比对 Banori 落库的 DownloadTask.InfoHash。
"""
import argparse
import hashlib
import io
import json
import os
import sys
import time


def encode(value):
    if isinstance(value, bool):
        raise TypeError("bencode 不支持 bool")
    if isinstance(value, int):
        return b"i%de" % value
    if isinstance(value, bytes):
        return b"%d:%s" % (len(value), value)
    if isinstance(value, str):
        data = value.encode("utf-8")
        return b"%d:%s" % (len(data), data)
    if isinstance(value, list):
        return b"l" + b"".join(encode(item) for item in value) + b"e"
    if isinstance(value, dict):
        items = []
        for key in sorted(value.keys(), key=lambda k: k.encode("utf-8") if isinstance(k, str) else k):
            items.append(encode(key) + encode(value[key]))
        return b"d" + b"".join(items) + b"e"
    raise TypeError("无法 bencode 的类型：%r" % type(value))


def iter_files(root):
    result = []
    for dir_path, dir_names, file_names in os.walk(root):
        dir_names.sort()
        for file_name in sorted(file_names):
            full = os.path.join(dir_path, file_name)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            result.append((rel, full))
    result.sort(key=lambda item: item[0])
    return result


def main():
    parser = argparse.ArgumentParser(description="生成 v1 .torrent（真实 piece SHA1）")
    parser.add_argument("--content-dir", required=True, help="内容文件所在目录")
    parser.add_argument("--output", required=True, help="输出 .torrent 路径")
    parser.add_argument("--announce", required=True, help="announce URL（隔离测试一律用 http://tracker.invalid/announce）")
    parser.add_argument("--name", help="多文件种子的根目录名（默认 content-dir 目录名；单文件种子忽略此参数）")
    parser.add_argument("--map", action="append", default=[], metavar="SRC=DST",
                        help="相对路径重映射 SRC=DST（可重复，DST 可含 / 分量）")
    parser.add_argument("--piece-length", type=int, default=262144)
    args = parser.parse_args()

    content_root = os.path.abspath(args.content_dir)
    if not os.path.isdir(content_root):
        print("内容目录不存在：%s" % content_root, file=sys.stderr)
        return 2

    rename = {}
    for mapping in args.map:
        if "=" not in mapping:
            print("--map 需要 SRC=DST 形式：%r" % mapping, file=sys.stderr)
            return 2
        src, dst = mapping.split("=", 1)
        rename[src.replace(os.sep, "/")] = dst

    files = iter_files(content_root)
    if not files:
        print("内容目录没有文件：%s" % content_root, file=sys.stderr)
        return 2

    entries = []
    for rel, full in files:
        torrent_rel = rename.get(rel, rel).lstrip("/")
        if not torrent_rel or torrent_rel.endswith("/"):
            print("重映射目标不是合法相对文件路径：%r -> %r" % (rel, torrent_rel), file=sys.stderr)
            return 2
        with open(full, "rb") as handle:
            data = handle.read()
        entries.append({
            "rel": torrent_rel,
            "data": data,
            "sha256": hashlib.sha256(data).hexdigest(),
            "size": len(data),
        })

    total = sum(entry["size"] for entry in entries)
    if total == 0:
        print("内容目录文件总大小为 0，无法生成种子。", file=sys.stderr)
        return 2

    stream = io.BytesIO(b"".join(entry["data"] for entry in entries))
    pieces = bytearray()
    piece_count = 0
    while True:
        block = stream.read(args.piece_length)
        if not block:
            break
        pieces.extend(hashlib.sha1(block).digest())
        piece_count += 1

    if len(entries) == 1:
        # 单文件种子（v1 标准）：文件名就是 info.name，没有 path 键、没有 files 列表。
        # qB/MonoTorrent 都按 info.name 落数据（实测 qB 4.6.4 数据文件 = savePath/info.name）；
        # 旧实现写的 info["path"] 非标准，会被 qB 忽略导致 recheck 找不到数据。
        # --name 在单文件下不生效（文件名优先），仅多文件根目录名用 --name。
        name = os.path.basename(entries[0]["rel"])
        info = {
            "name": name,
            "piece length": args.piece_length,
            "pieces": bytes(pieces),
            "length": entries[0]["size"],
        }
    else:
        name = args.name or os.path.basename(content_root.rstrip("/\\"))
        info = {
            "name": name,
            "piece length": args.piece_length,
            "pieces": bytes(pieces),
            "files": [{"length": entry["size"], "path": entry["rel"].split("/")}
                      for entry in entries],
        }

    info_hash = hashlib.sha1(encode(info)).hexdigest()

    torrent = {
        "announce": args.announce,
        "info": info,
        "comment": "Banori e2e test torrent",
        "created by": "Banori e2e make_torrent.py",
        "creation date": int(time.time()),
    }
    output_path = os.path.abspath(args.output)
    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    with open(output_path, "wb") as handle:
        handle.write(encode(torrent))

    payload = {
        "info_hash": info_hash,
        "name": name,
        "announce": args.announce,
        "total_size": total,
        "piece_count": piece_count,
        "piece_length": args.piece_length,
        "torrent_path": output_path,
        "files": [{"path": entry["rel"], "sha256": entry["sha256"], "size": entry["size"]}
                  for entry in entries],
    }
    print(json.dumps(payload, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
