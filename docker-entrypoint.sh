#!/bin/sh
# Bind-mounted volumes arrive owned by whatever created them on the host (usually
# root), which overrides the image's build-time `chown app:app /data`. If we're
# root, take ownership of the data dir, then drop to the non-root "app" user
# before exec'ing the app. If we're already non-root (compose `user:` override),
# just run as-is and rely on the host dir being writable.
set -e

DATA_DIR="${QBITFLOW_DATA_DIR:-/data}"
LOG_DIR="${QBITFLOW_LOG_DIR:-/log}"
mkdir -p "$DATA_DIR" "$LOG_DIR"

if [ "$(id -u)" = "0" ]; then
    chown -R app:app "$DATA_DIR" "$LOG_DIR"
    exec gosu app "$@"
fi

exec "$@"
