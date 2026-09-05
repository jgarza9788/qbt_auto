#!/bin/sh
# Bind mounts arrive owned by whatever uid the host used, which masks the
# ownership set at build time. Fix it as root, then drop privileges -- the app
# itself never runs as root.
set -e

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

if [ "$(id -u)" = "0" ]; then
    if [ "$PGID" != "$(id -g qbitflow)" ]; then
        groupmod -o -g "$PGID" qbitflow
    fi
    if [ "$PUID" != "$(id -u qbitflow)" ]; then
        usermod -o -u "$PUID" qbitflow
    fi

    for d in /data /data/keys /exports /scripts; do
        [ -d "$d" ] || mkdir -p "$d"
        # Only the directory itself, not a recursive walk: -R over an exports
        # directory that has accumulated thousands of files makes every restart
        # slower than the last. The sentinel catches a genuinely new volume.
        chown "$PUID:$PGID" "$d" 2>/dev/null || true
        if [ ! -f "$d/.qbitflow-owned" ]; then
            chown -R "$PUID:$PGID" "$d" 2>/dev/null || true
            touch "$d/.qbitflow-owned" 2>/dev/null || true
        fi
    done

    exec gosu qbitflow "$@"
fi

exec "$@"
