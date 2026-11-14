# -*- coding: utf-8 -*-
"""qB 4.6.4 关键行为探针（场景1/2 前置实测，一次性，不属常驻工具链）。

实测三件事，全部决定跨站搬运复用场景能否成立：
  1. app preferences 的 preallocate_all_files 默认值——预分配会决定「无数据种子改名」
     是纯名字变更还是真实移动稀疏文件。
  2. 停止态添加无数据种子后，经 /torrents/renameFile 把文件改名到已有真实文件的路径：
     改名是否生效、被占用的目标文件内容是否原样保留、recheck 后是否判定完整。
  3. 停止态添加已有数据种子后直接 resume（不手工 recheck）：qB 是否自动校验并到做种态
     （场景2 C 的「正常完整链」依赖此行为）。

用法：python -I probe_rename.py --scene-dir <场景 downloads 宿主目录> --webui-port 18085
"""
import argparse
import hashlib
import http.client
import json
import os
import random
import string
import sys
import time

BASE = "/api/v2"


def req(port, method, path, cookie="", fields=None):
    headers = {"Referer": f"http://127.0.0.1:{port}"}
    body = None
    if fields is not None:
        body = "&".join(f"{k}={v}" for k, v in fields.items())
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    if cookie:
        headers["Cookie"] = cookie
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=15)
    try:
        conn.request(method, path, body=body, headers=headers)
        response = conn.getresponse()
        payload = response.read()
        return response.status, payload, response.getheader("set-cookie") or ""
    finally:
        conn.close()


def login(port, username="admin", password="e2e-s1-password"):
    status, _, set_cookie = req(port, "POST", f"{BASE}/auth/login",
                                fields={"username": username, "password": password})
    for part in set_cookie.split(";"):
        if part.strip().startswith("SID="):
            return part.strip()
    raise RuntimeError(f"login failed: HTTP {status}")


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def wait_for(predicate, timeout_seconds, what):
    deadline = time.monotonic() + timeout_seconds
    last = None
    while time.monotonic() < deadline:
        last = predicate()
        if last:
            return last
        time.sleep(0.5)
    raise RuntimeError(f"等待 {what} 超时（最后观察：{last!r}）")


def bencode_torrent(name, data, piece_length, announce):
    import hashlib as _h
    pieces = b""
    for offset in range(0, len(data), piece_length):
        pieces += _h.sha1(data[offset:offset + piece_length]).digest()
    info = {"name": name, "piece length": piece_length, "pieces": pieces, "length": len(data)}
    def enc(value):
        if isinstance(value, int):
            return b"i%de" % value
        if isinstance(value, bytes):
            return b"%d:%s" % (len(value), value)
        if isinstance(value, str):
            raw = value.encode("utf-8")
            return b"%d:%s" % (len(raw), raw)
        if isinstance(value, list):
            return b"l" + b"".join(enc(item) for item in value) + b"e"
        items = []
        for key in sorted(value.keys(), key=lambda item: item.encode("utf-8")):
            items.append(enc(key) + enc(value[key]))
        return b"d" + b"".join(items) + b"e"
    info_hash = _h.sha1(enc(info)).hexdigest()
    return info_hash, enc({"announce": announce, "info": info})


def req_files(port, sid, info_hash):
    # qB 4.6.4 的 torrents/files 只认 POST 表单（GET 带 body 会 400，实测）。
    return req(port, "POST", f"{BASE}/torrents/files", sid, fields={"hash": info_hash})


def req_info(port, sid):
    status, payload, _ = req(port, "GET", f"{BASE}/torrents/info", sid)
    return json.loads(payload)


def dump_timeline(port, sid, info_hash, seconds):
    seen = []
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        torrent = next((item for item in req_info(port, sid)
                        if item.get("hash", "").lower() == info_hash), None)
        if torrent is None:
            state = "missing"
        else:
            state = torrent.get("state")
        if not seen or seen[-1][1] != state:
            seen.append((round(time.monotonic(), 1), state,
                         torrent.get("progress") if torrent else None,
                         torrent.get("amount_left") if torrent else None))
        time.sleep(0.3)
    return seen


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--scene-dir", required=True)
    parser.add_argument("--webui-port", type=int, default=18085)
    parser.add_argument("--password", default="e2e-s1-password")
    args = parser.parse_args()

    downloads = os.path.abspath(os.path.join(args.scene_dir, "downloads"))
    os.makedirs(os.path.join(downloads, "Media", "Anime"), exist_ok=True)

    sid = login(args.webui_port, password=args.password)

    status, payload, _ = req(args.webui_port, "GET", f"{BASE}/app/preferences", sid)
    prefs = json.loads(payload)
    print("alloc keys:", {key: value for key, value in prefs.items() if "alloc" in key.lower()})
    print("dht =", prefs.get("dht"), "pex =", prefs.get("pex"), "lsd =", prefs.get("lsd"))

    # —— 探针 1：无数据种子改名 onto 既有真实文件（候选数据与种子内容一致，同真实复用场景）——
    source_data = bytes(1_111_111)  # 确定性内容（全 0x01）
    source_name = "probe-src.mkv"
    target_data = source_data  # 占用文件与种子内容一致（跨站搬运复用的前提）
    target_path = os.path.join(downloads, "Media", "Anime", "probe-target.mkv")
    with open(target_path, "wb") as handle:
        handle.write(target_data)
    target_digest = sha256_of(target_path)
    info_hash, torrent_bytes = bencode_torrent(source_name, source_data, 131072,
                                               "http://tracker.invalid/announce")

    # multipart add（stopped）
    boundary = "----probe" + "".join(random.choices(string.ascii_letters, k=16))
    parts = []
    for field, value in (("savepath", "/downloads"), ("autoTMM", "false"),
                         ("paused", "true"), ("stopped", "true"),
                         ("skip_checking", "false"), ("contentLayout", "Original")):
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{field}\"\r\n\r\n{value}\r\n".encode())
    parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"torrents\"; "
                 f"filename=\"probe.torrent\"\r\nContent-Type: application/x-bittorrent\r\n\r\n".encode())
    parts.append(torrent_bytes + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    headers = {"Cookie": sid, "Referer": f"http://127.0.0.1:{args.webui_port}",
               "Content-Type": f"multipart/form-data; boundary={boundary}"}
    conn = http.client.HTTPConnection("127.0.0.1", args.webui_port, timeout=15)
    conn.request("POST", f"{BASE}/torrents/add", body=b"".join(parts), headers=headers)
    add_response = conn.getresponse()
    add_body = add_response.read().decode()
    conn.close()
    print("add:", add_response.status, add_body)

    wait_for(lambda: any("hash" in item for item in req_info(args.webui_port, sid)), 30, "种子可见")
    torrents = req_info(args.webui_port, sid)
    print("added state:", torrents[0].get("state"), "progress:", torrents[0].get("progress"))

    source_on_disk = os.path.join(downloads, source_name)
    print("source file exists on disk (pre-allocation?):", os.path.exists(source_on_disk))

    status, payload, _ = req_files(args.webui_port, sid, info_hash)
    print("files before rename:", payload.decode())

    status, payload, _ = req(args.webui_port, "POST", f"{BASE}/torrents/renameFile", sid,
                             fields={"hash": info_hash, "oldPath": source_name,
                                     "newPath": "Media/Anime/probe-target.mkv"})
    print("renameFile:", status, payload.decode()[:200])

    time.sleep(1.5)
    status, payload, _ = req_files(args.webui_port, sid, info_hash)
    print("files after rename:", payload.decode())
    print("target intact after rename:", sha256_of(target_path) == target_digest)
    print("source file still exists:", os.path.exists(source_on_disk))

    print("timeline before recheck:", dump_timeline(args.webui_port, sid, info_hash, 3))
    req(args.webui_port, "POST", f"{BASE}/torrents/recheck", sid, fields={"hashes": info_hash})
    try:
        torrent = wait_for(lambda: (_t := _torrent_by_hash(args.webui_port, sid, info_hash)) and
                                    _t.get("amount_left") == 0 and _t.get("progress") == 1.0 and _t,
                           60, "recheck 完成")
        print("progress after recheck:", torrent.get("progress"), "amount_left:", torrent.get("amount_left"))
    except RuntimeError:
        print("recheck 未完成；timeline:", dump_timeline(args.webui_port, sid, info_hash, 3))
        print("full info:", json.dumps(_torrent_by_hash(args.webui_port, sid, info_hash), ensure_ascii=False)[:1200])

    req(args.webui_port, "POST", f"{BASE}/torrents/resume", sid, fields={"hashes": info_hash})
    time.sleep(2.0)
    print("state after resume:", _torrent_state(args.webui_port, sid, info_hash))

    # —— 探针 2：已有数据的种子，停止态添加后直接 resume（不 recheck）——
    resume_data = bytes(2) * 700_000
    resume_name = "probe-resume.mkv"
    info_hash2, torrent_bytes2 = bencode_torrent(resume_name, resume_data, 262144,
                                                 "http://tracker.invalid/announce")
    with open(os.path.join(downloads, resume_name), "wb") as handle:
        handle.write(resume_data)

    boundary = "----probe" + "".join(random.choices(string.ascii_letters, k=16))
    parts = []
    for field, value in (("savepath", "/downloads"), ("autoTMM", "false"),
                         ("paused", "true"), ("stopped", "true"),
                         ("skip_checking", "false"), ("contentLayout", "Original")):
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{field}\"\r\n\r\n{value}\r\n".encode())
    parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"torrents\"; "
                 f"filename=\"probe2.torrent\"\r\nContent-Type: application/x-bittorrent\r\n\r\n".encode())
    parts.append(torrent_bytes2 + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    headers = {"Cookie": sid, "Referer": f"http://127.0.0.1:{args.webui_port}",
               "Content-Type": f"multipart/form-data; boundary={boundary}"}
    conn = http.client.HTTPConnection("127.0.0.1", args.webui_port, timeout=15)
    conn.request("POST", f"{BASE}/torrents/add", body=b"".join(parts), headers=headers)
    add2 = conn.getresponse()
    print("add2:", add2.status, add2.read().decode()[:60])
    conn.close()

    wait_for(lambda: _torrent_by_hash(args.webui_port, sid, info_hash2), 30, "种子2可见")
    print("state2 at add:", _torrent_state(args.webui_port, sid, info_hash2))
    req(args.webui_port, "POST", f"{BASE}/torrents/resume", sid, fields={"hashes": info_hash2})

    def resume_done():
        torrent = _torrent_by_hash(args.webui_port, sid, info_hash2)
        if torrent and torrent.get("progress") == 1.0:
            return torrent
        return False
    try:
        torrent = wait_for(resume_done, 60, "resume 后自动校验完成")
        print("state2 after resume:", torrent.get("state"), "progress:", torrent.get("progress"))
    except RuntimeError as error:
        print("resume 自动校验未发生：", error)
        print("state2 now:", _torrent_state(args.webui_port, sid, info_hash2))

    # 清理探针种子与文件
    for probe_hash in (info_hash, info_hash2):
        req(args.webui_port, "POST", f"{BASE}/torrents/delete", sid,
            fields={"hashes": probe_hash, "deleteFiles": "true"})
    for leftover in (target_path, os.path.join(downloads, resume_name)):
        if os.path.exists(leftover):
            os.remove(leftover)
    print("probe done")
    return 0


def _torrents(port, sid):
    status, payload, _ = req(port, "GET", f"{BASE}/torrents/info", sid)
    return json.loads(payload)


def _torrent_by_hash(port, sid, info_hash):
    return next((item for item in _torrents(port, sid)
                 if item.get("hash", "").lower() == info_hash), None)


def _torrent_state(port, sid, info_hash):
    torrent = _torrent_by_hash(port, sid, info_hash)
    return torrent.get("state") if torrent else None


if __name__ == "__main__":
    sys.exit(main())
