#!/bin/sh
set -e

# 以 root 启动时把 /app/data 属主修正为 PUID:PGID 再降权运行，
# 使 bind mount 到 root 属主目录（Docker daemon 自动创建）时无需宿主机手工 chown；
# 非 root 启动（如 compose 指定 user:）则跳过修正，目录属主须自行保证一致。
if [ "$(id -u)" = "0" ]; then
    PUID="${PUID:-1654}"
    PGID="${PGID:-1654}"
    case "$PUID$PGID" in
        *[!0-9]*)
            echo "[entrypoint] PUID/PGID 必须是数字：PUID=$PUID PGID=$PGID" >&2
            exit 1
            ;;
    esac
    mkdir -p /app/data
    chown -R "$PUID:$PGID" /app/data
    exec setpriv --reuid "$PUID" --regid "$PGID" --clear-groups -- "$@"
fi

exec "$@"
