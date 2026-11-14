# -*- coding: utf-8 -*-
"""隔离 qBittorrent Docker 容器管理器（冒烟/验收共用）。

硬边界：绝不触碰宿主 qbittorrent.exe（不杀进程、不要求退出；隔离性来自只连
127.0.0.1:<webui_port>、专用凭据、独立容器与数据目录）。up 前校验选定端口空闲，
宿主 qB 端口冲突或第三方占用一律报错退出；宿主 qB 在跑但无端口冲突时仅警告并继续。
所有 qB 一律 linuxserver/qbittorrent Docker 容器，默认 tag 4.6.4
（与用户 NAS 生产一致，首要兼容目标）；--image-tag 可换 latest（5.x）做交叉验证。

用法（python -I 运行）：
    启动：  python -I qb_container.py up \
                --name banori-accept-qb-s1 \
                --scene-dir <场景目录（含 config/ downloads/）> \
                --webui-port 18081 --bt-port 16881 \
                --username admin --password <明文> \
                --state <state.json>
    停止：  python -I qb_container.py down --state <state.json>   （保留卷数据）
    删除：  python -I qb_container.py down --state <state.json> --remove
    状态：  python -I qb_container.py status --state <state.json>

「up」流程（linuxserver/qbittorrent:4.6.4，qBittorrent 4.6.4）：
  1. docker pull 镜像（幂等）。
  2. 预写 <scene-dir>/config/qBittorrent/qBittorrent.conf：WebUI 节固定
     Username 与 Password_PBKDF2（key = PBKDF2-HMAC-SHA512(password, salt,
     100000 iter, 64 bytes)，salt 16 字节；salt/key 各自 base64 后冒号连接，
     外层 @ByteArray(...)。此格式 4.6.x 与 5.x 均认）。预写凭据比「读首启日志
     临时密码再 app/setPreferences 固定」更稳：不依赖日志解析，重启不丢密码。
     同时关掉首启向导（LegalNotice\\Accepted=true）。
  3. docker run -d --name <name> --security-opt no-new-privileges：
     容器内外同端口（4.6.x Host 校验要求 Host 头端口与 qB 实际监听端口一致，
     127.0.0.1:<webui>:8081 这种宿主≠容器的映射会被「Invalid Host header,
     port mismatch」全拒，见 qb-notes.md）：-p 127.0.0.1:<webui>:<webui>
     -p 127.0.0.1:<bt>:<bt>/tcp+udp（只绑回环），
     --add-host tracker.byr.pt:127.0.0.1（站点分类要求 announce 用真实 PT 域名，
     容器内解析到回环即零真实 PT 流量；conf 同时关 DHT/PEX/LSD），
     -v <scene-dir>/config:/config -v <scene-dir>/downloads:/downloads，
     -e WEBUI_PORT=<webui> -e TORRENTING_PORT=<bt> -e PUID=1000 -e PGID=1000 -e TZ。
  4. 轮询 WebUI 就绪并登录验证凭据（4.6.4 登录成功 = HTTP 200 +「Ok.」+ SID，
     失败 = 200 +「Fails.」；IP 连续失败 5 次会被临时封禁，轮询间隔 1s 不会触发）。
  5. sanity：确认选定端口空闲（被占用即拒绝）；宿主 qbittorrent.exe 在跑仅警告
     （端口无冲突即可），不阻止启动。

state JSON 字段：container_name、image、base_url、webui_port、bt_port、username、
password（明文，仅本机隔离实例）、scene_dir、app_version、created_at。
「down」只对 state 中记录的容器名执行 docker stop/rm（-v 清匿名卷），不碰其他容器。
"""
import argparse
import base64
import hashlib
import http.client
import json
import os
import subprocess
import sys
import time

DEFAULT_IMAGE_TAG = "4.6.4"


def pbkdf2_stored_value(password: str) -> str:
    """qB 认可的 Password_PBKDF2 值：b64(salt):b64(key)，100000 迭代 SHA512。"""
    salt = os.urandom(16)
    digest = hashlib.pbkdf2_hmac("sha512", password.encode("utf-8"), salt, 100000, dklen=64)
    return "{}:{}".format(base64.b64encode(salt).decode(),
                          base64.b64encode(digest).decode())


def write_conf(scene_dir: str, username: str, password: str, webui_port: int, bt_port: int) -> str:
    config_dir = os.path.join(scene_dir, "config", "qBittorrent")
    os.makedirs(config_dir, exist_ok=True)
    conf_path = os.path.join(config_dir, "qBittorrent.conf")
    conf = (
        "[AutoRun]\n"
        "enabled=false\n\n"
        "[BitTorrent]\n"
        "Session\\DefaultSavePath=/downloads\n"
        "Session\\Port={}\n".format(bt_port) +
        "Session\\QueueingSystemEnabled=false\n"
        # 隔离验收：关闭全部 peer 发现途径，种子只可能向 announce 里的 tracker 汇报——
        # 而 tracker.byr.pt 已被 --add-host 指向容器自身回环（announce 立刻连接失败）。
        "Session\\DHTEnabled=false\n"
        "Session\\PeXEnabled=false\n"
        "Session\\LSDEnabled=false\n\n"
        "[LegalNotice]\n"
        "Accepted=true\n\n"
        "[Meta]\n"
        "MigrationVersion=6\n\n"
        "[Preferences]\n"
        # 4.6.x 的 Host 校验按「Host 头端口 == qB 实际监听端口」判断，与
        # HostHeaderValidation 无关（宿主≠容器端口的映射一律 401「Invalid Host header,
        # port mismatch」）；故容器内外同端口：WEBUI_PORT 与 -p 都用宿主端口值。
        # HostHeaderValidation=false 保留，兼容 5.x 行为差异。
        "WebUI\\Address=*\n"
        "WebUI\\Port={}\n".format(webui_port) +
        "WebUI\\HostHeaderValidation=false\n"
        "WebUI\\CSRFProtection=false\n"
        "WebUI\\Username={}\n".format(username) +
        'WebUI\\Password_PBKDF2="@ByteArray({})"\n'.format(pbkdf2_stored_value(password)) +
        "WebUI\\Enabled=true\n"
    )
    with open(conf_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(conf)
    return conf_path


def run_docker(args_list, timeout=300):
    completed = subprocess.run(["docker"] + args_list, capture_output=True, text=True, timeout=timeout)
    return completed.returncode, completed.stdout.strip(), completed.stderr.strip()


def http_request(port: int, method: str, path: str, cookie: str = "", body: str = ""):
    headers = {"Referer": f"http://127.0.0.1:{port}"}
    if cookie:
        headers["Cookie"] = cookie
    if body:
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    try:
        conn.request(method, path, body=body or None, headers=headers)
        response = conn.getresponse()
        payload = response.read().decode("utf-8", "replace")
        set_cookie = response.getheader("set-cookie") or ""
        return response.status, payload, set_cookie
    finally:
        conn.close()


def wait_webui_ready(port: int, username: str, password: str, timeout_seconds: float):
    """轮询 WebUI 就绪并登录验证；返回 (app_version, cookie)。

    qB 4.6.4 登录成功实测返回 HTTP 200 + body「Ok.」+ SID Cookie（非 204）；
    失败是 200 +「Fails.」，IP 封禁后 403。所以判定以 SID + body 为准，不锁状态码。
    """
    deadline = time.monotonic() + timeout_seconds
    last_error = None
    while time.monotonic() < deadline:
        try:
            status, payload, set_cookie = http_request(
                port, "POST", "/api/v2/auth/login",
                body=f"username={username}&password={password}")
            sid = ""
            for part in set_cookie.split(";"):
                if part.strip().startswith("SID="):
                    sid = part.strip()
                    break
            if status in (200, 204) and sid and payload.strip() == "Ok.":
                v_status, version, _ = http_request(port, "GET", "/api/v2/app/version", sid)
                if v_status == 200:
                    return version.strip(), sid
                last_error = f"version HTTP {v_status}: {version!r}"
            elif status == 200 and payload.strip() == "Fails.":
                last_error = "凭据被拒（Fails.）"
            elif status in (403, 401):
                # 凭据被拒或 IP 被临时封禁：容器可能用旧配置起的（config 卷残留），如实报错。
                last_error = f"凭据被拒（HTTP {status}）"
            else:
                last_error = f"login HTTP {status}: {payload!r}"
        except OSError as error:
            last_error = str(error)
        time.sleep(1.0)
    raise RuntimeError(f"WebUI 在 {timeout_seconds}s 内未就绪：{last_error}")


def host_qbittorrent_running() -> bool:
    try:
        output = subprocess.run(["tasklist", "/FI", "IMAGENAME eq qbittorrent.exe"],
                                capture_output=True, text=True, timeout=15).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    return "qbittorrent.exe" in output


def host_qbittorrent_pids() -> list:
    """宿主 qbittorrent.exe 的 PID 列表（只读 tasklist 查询，非空即宿主 qB 在运行）。"""
    try:
        output = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq qbittorrent.exe", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=15).stdout
    except (OSError, subprocess.SubprocessError):
        return []
    pids = []
    for line in output.splitlines():
        fields = [field.strip() for field in line.split(",")]
        if len(fields) >= 2 and fields[0] == '"qbittorrent.exe"':
            pids.append(fields[1].strip('"'))
    return pids


def listening_ports_of(pids: list) -> set:
    """netstat -ano 枚举给定 PID 监听（LISTENING）或绑定的端口集合（tcp+udp，只读）。"""
    try:
        output = subprocess.run(["netstat", "-ano"],
                                capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        return set()
    wanted = set(pids)
    ports = set()
    for line in output.splitlines():
        fields = line.split()
        if len(fields) < 4 or fields[-1] not in wanted:
            continue
        if fields[0] == "UDP" or (fields[0] == "TCP" and fields[3] == "LISTENING"):
            local = fields[1]
            if local.count(":"):
                ports.add(local.rsplit(":", 1)[1])
    return ports


def port_in_use(port: int) -> bool:
    """netstat 检查端口当前是否被监听/绑定（防第三方占用）。

    只认 TCP LISTENING 与 UDP 绑定行：回环连接关闭后，服务端 socket 会以 TIME_WAIT
    残留几分钟（本地地址仍是本端口），按原样计入会把刚清理完的端口误判为被占用。
    """
    try:
        output = subprocess.run(["netstat", "-ano"],
                                capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    suffix = ":{}".format(port)
    for line in output.splitlines():
        fields = line.split()
        if len(fields) < 4 or not fields[1].endswith(suffix):
            continue
        if fields[0] == "UDP" or (fields[0] == "TCP" and fields[3] == "LISTENING"):
            return True
    return False


def emit(payload):
    print(json.dumps(payload, ensure_ascii=False))


def fail(message):
    emit({"status": "error", "message": message})
    return 1


def container_exists(name: str) -> bool:
    code, out, _ = run_docker(["ps", "-a", "--format", "{{.Names}}"])
    return code == 0 and name in out.splitlines()


def command_up(args):
    # 隔离前提：测试只连 http://127.0.0.1:<webui_port>（仅回环）、专用凭据、独立容器与
    # 数据目录；宿主 qB 是否运行不影响隔离性。保护意图是防端口冲突与误触宿主实例——
    # 宿主 qB 在跑时只警告，真正阻塞启动的是「我们要绑的端口已被占用」。
    if host_qbittorrent_running():
        host_ports = listening_ports_of(host_qbittorrent_pids())
        clash = host_ports & {str(args.webui_port), str(args.bt_port)}
        if clash:
            return fail("宿主 qbittorrent.exe 占用了本容器要绑定的端口（{}），"
                        "请换 --webui-port/--bt-port 后重试。".format(", ".join(sorted(clash))))
        print("警告：宿主 qBittorrent 正在运行（监听端口 {} 与本任务端口无冲突），"
              "测试仅连 127.0.0.1:{}（专用凭据、独立容器与数据目录），继续隔离容器启动。"
              .format(sorted(host_ports) or "未知", args.webui_port), file=sys.stderr)

    for label, port in (("webui", args.webui_port), ("bt", args.bt_port)):
        if port_in_use(port):
            return fail("端口 {}（{}）已被其他进程占用，请换端口后重试。".format(port, label))

    image = f"linuxserver/qbittorrent:{args.image_tag}"
    code, out, err = run_docker(["pull", image])
    if code != 0:
        return fail(f"docker pull {image} 失败：{err or out}")

    scene_dir = os.path.abspath(args.scene_dir)
    config_dir = os.path.join(scene_dir, "config")
    downloads_dir = os.path.join(scene_dir, "downloads")
    os.makedirs(downloads_dir, exist_ok=True)
    conf_path = write_conf(scene_dir, args.username, args.password, args.webui_port, args.bt_port)

    if container_exists(args.name):
        run_docker(["rm", "-f", args.name])

    run_args = [
        "run", "-d",
        "--name", args.name,
        "--security-opt", "no-new-privileges",
        # 隔离：announce 用真实 PT 域名（Banori 站点分类要求精确主机名 tracker.byr.pt），
        # 容器内把该域名解析到自身回环，announce 立即连接失败，零真实 PT 流量。
        "--add-host", "tracker.byr.pt:127.0.0.1",
        "-p", f"127.0.0.1:{args.webui_port}:{args.webui_port}",
        "-p", f"127.0.0.1:{args.bt_port}:{args.bt_port}/tcp",
        "-p", f"127.0.0.1:{args.bt_port}:{args.bt_port}/udp",
        "-v", f"{config_dir}:/config".replace("\\", "/"),
        "-v", f"{downloads_dir}:/downloads".replace("\\", "/"),
        "-e", f"WEBUI_PORT={args.webui_port}",
        "-e", f"TORRENTING_PORT={args.bt_port}",
        "-e", "PUID=1000",
        "-e", "PGID=1000",
        "-e", f"TZ={args.tz}",
        image,
    ]
    code, out, err = run_docker(run_args)
    if code != 0:
        return fail(f"docker run 失败：{err or out}")

    state = {
        "container_name": args.name,
        "image": image,
        "base_url": f"http://127.0.0.1:{args.webui_port}",
        "webui_port": args.webui_port,
        "bt_port": args.bt_port,
        "username": args.username,
        "password": args.password,
        "scene_dir": scene_dir,
        "conf_path": conf_path,
        "created_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
    }
    with open(args.state, "w", encoding="utf-8") as handle:
        json.dump(state, handle, ensure_ascii=False, indent=2)

    try:
        version, _ = wait_webui_ready(args.webui_port, args.username, args.password, 120.0)
    except Exception:
        run_docker(["rm", "-f", args.name])
        raise

    state["app_version"] = version
    with open(args.state, "w", encoding="utf-8") as handle:
        json.dump(state, handle, ensure_ascii=False, indent=2)
    emit({"status": "up", "container": args.name, "webui": state["base_url"],
          "app_version": version, "image": image,
          "host_qbittorrent_running": host_qbittorrent_running()})
    return 0


def load_state(path: str) -> dict:
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def command_down(args):
    state = load_state(args.state)
    name = state["container_name"]
    stopped = removed = True
    if container_exists(name):
        code, out, err = run_docker(["stop", name], timeout=120)
        stopped = code == 0
        if args.remove:
            code, out, err = run_docker(["rm", "-v", name], timeout=120)
            removed = code == 0
    emit({"status": "down", "container": name, "stopped": stopped,
          "removed": args.remove and removed})
    return 0 if stopped and (not args.remove or removed) else 1


def command_status(args):
    state = load_state(args.state)
    name = state["container_name"]
    emit({"status": "status", "container": name,
          "exists": container_exists(name),
          "app_version": state.get("app_version"),
          "webui": state["base_url"],
          "host_qbittorrent_running": host_qbittorrent_running()})
    return 0


def main():
    parser = argparse.ArgumentParser(description="隔离 qBittorrent Docker 容器管理")
    sub = parser.add_subparsers(dest="command", required=True)

    up = sub.add_parser("up")
    up.add_argument("--name", required=True, help="容器名（如 banori-accept-qb-s1）")
    up.add_argument("--scene-dir", required=True, help="场景目录（其下 config/ downloads/ 挂给容器）")
    up.add_argument("--webui-port", type=int, required=True, help="宿主侧 WebUI 端口（只绑 127.0.0.1）")
    up.add_argument("--bt-port", type=int, required=True, help="宿主侧 BT 端口（只绑 127.0.0.1）")
    up.add_argument("--username", default="admin")
    up.add_argument("--password", required=True)
    up.add_argument("--image-tag", default=DEFAULT_IMAGE_TAG, help="镜像 tag（4.6.4 / latest）")
    up.add_argument("--tz", default="Asia/Shanghai")
    up.add_argument("--state", required=True, help="state JSON 输出路径")

    down = sub.add_parser("down")
    down.add_argument("--state", required=True)
    down.add_argument("--remove", action="store_true", help="停止并删除容器（-v 清匿名卷）")

    status = sub.add_parser("status")
    status.add_argument("--state", required=True)

    args = parser.parse_args()
    if args.command == "up":
        return command_up(args)
    if args.command == "down":
        return command_down(args)
    return command_status(args)


if __name__ == "__main__":
    sys.exit(main())
