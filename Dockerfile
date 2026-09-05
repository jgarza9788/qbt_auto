# syntax=docker/dockerfile:1

# --------------------------------------------------------------------------- #
# CSS. Pinned to the *build* platform: stylesheets are architecture-independent,
# so running this natively avoids an emulated Node build on the arm64 leg.
# --------------------------------------------------------------------------- #
FROM --platform=$BUILDPLATFORM node:22-alpine AS css
WORKDIR /build
COPY package.json tailwind.config.js ./
RUN npm install --no-audit --no-fund
COPY src/qbitflow/web ./src/qbitflow/web
RUN npx tailwindcss -i src/qbitflow/web/static/app.src.css -o /out/app.css --minify

# --------------------------------------------------------------------------- #
# Python dependencies, resolved into a self-contained virtualenv.
# --------------------------------------------------------------------------- #
FROM python:3.12-slim AS deps
COPY --from=ghcr.io/astral-sh/uv:0.9.21 /uv /usr/local/bin/uv
ENV UV_LINK_MODE=copy UV_COMPILE_BYTECODE=1 UV_PYTHON_DOWNLOADS=never
WORKDIR /app
# Dependencies change far less often than source, so resolve them on their own layer.
COPY pyproject.toml README.md ./
RUN --mount=type=cache,target=/root/.cache/uv \
    uv venv /opt/venv && VIRTUAL_ENV=/opt/venv uv pip install -r pyproject.toml

# --------------------------------------------------------------------------- #
# Runtime.
# --------------------------------------------------------------------------- #
FROM python:3.12-slim AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gosu \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd -g 1000 qbitflow && useradd -u 1000 -g 1000 -m -s /bin/bash qbitflow

ENV VIRTUAL_ENV=/opt/venv \
    PATH=/opt/venv/bin:$PATH \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    QBITFLOW_DATA_DIR=/data \
    QBITFLOW_EXPORTS_DIR=/exports \
    QBITFLOW_HOST=0.0.0.0 \
    QBITFLOW_PORT=8080

COPY --from=deps /opt/venv /opt/venv

WORKDIR /app
COPY pyproject.toml README.md alembic.ini ./
COPY alembic ./alembic
COPY src ./src
COPY --from=css /out/app.css ./src/qbitflow/web/static/app.css

# Installed non-editable so the package resolves without src/ being on sys.path.
RUN pip install --no-deps --no-cache-dir -e . \
    && mkdir -p /data /data/keys /exports /scripts \
    && chown qbitflow:qbitflow /data /data/keys /exports /scripts

COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
# Windows checkouts hand us CRLF line endings, which make the shebang unusable.
RUN sed -i 's/\r$//' /usr/local/bin/docker-entrypoint.sh \
    && chmod +x /usr/local/bin/docker-entrypoint.sh

EXPOSE 8080

# Liveness only. /healthz touches no dependency, so a database problem does not
# get "fixed" by restarting the container in a loop. Start period covers
# migrations on a cold volume.
HEALTHCHECK --interval=30s --timeout=5s --start-period=45s --retries=5 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["qbitflow"]
