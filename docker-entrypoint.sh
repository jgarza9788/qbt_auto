#!/bin/sh
set -e

# Bind-mounted volumes (/data, /exports, ...) arrive owned by the host user,
# which masks the ownership set at build time. Fix it here, then drop to "app".
if [ "$(id -u)" = "0" ]; then
  for d in /data /data/keys /exports /scripts; do
    [ -d "$d" ] || mkdir -p "$d"
    chown -R app:app "$d" 2>/dev/null || true
  done
  exec gosu app "$@"
fi

exec "$@"
