Branch: Dev9 (was an empty tree). NOTHING COMMITTED YET — all files untracked.

I can't write files — I'm a side responder with no tools, and the main agent is still working in the background. Here's the handoff content to save (suggest RESUME.md at the repo root), plus what you should know.

Important: the main agent may still be mid-write on Phase 6 files. Let it land before you stop, and note that nothing is committed yet — all of this is untracked in the working tree on branch Dev9.

# qbitflow — resume notes (break at ~18:00)

Plan file: C:\Users\JGarza\.claude\plans\read-c-users-jgarza-github-qbt-auto-dev-zazzy-wigderson.md
Branch: Dev9 (was an empty tree). NOTHING COMMITTED YET — all files untracked.

## Locked decisions
- Python 3.12 + FastAPI, fresh build. Dev8 (C# QbitFlow, 157 files) is reference only.
- Frontend: htmx + Alpine + Jinja2, Tailwind compiled at image-build time. No Node at runtime.
- Full Dev8 action parity: 11 handlers. script.run gated behind QBITFLOW_ENABLE_SCRIPT_ACTION (default off).
- No legacy config.json import.
- Jellystat/Jellyglance adapters built with UI-configurable endpoints + field maps (APIs unverified offline).
- Two premises in the original prompt were false: Dev8 has no real auth (single shared secret,
  unsalted SHA-256, fails open) and no SQL compilation (DynamicExpresso, row-by-row).

## Done — Phases 0–5. 139 tests passing, ruff clean.
- Phase 0: config.py (two-tier), crypto.py (AES-GCM, real encryption by default), db/{base,types,models,migrate}.py,
  Alembic initial migration, logging.py, api/health.py, Dockerfile, docker-entrypoint.sh (gosu + PUID/PGID),
  docker-compose.yml, .dockerignore, .gitignore, package.json, tailwind.config.js
- Phase 1: sources/{base,http,qbt,storage,plex,jellyfin,tautulli,rest_history,paths,registry}.py
- Phase 2: snapshot/{schema,mediakey,matching,builder,udf}.py
- Phase 3: conditions/{registry,tree,compiler,rawsql,execute}.py — 80 fields generated from schema
- Phase 4: actions/{base,handlers}.py (11 handlers), engine/{cooldown,logbus,runner}.py
- Phase 5: engine/{schedule_text,scheduler}.py; engine wired into main.py lifespan

## In progress — Phase 6 (auth + UI)
Done: auth/{passwords,sessions,middleware}.py, api/deps.py, api/meta.py

REMAINING:
1. api/: rules.py (CRUD + compile + test-against-torrents), sources.py (CRUD + test + sample),
   runs.py (list, detail, SSE stream), engine.py (run now, enable/disable, status), settings.py
2. auth routes: /setup wizard, /login, /logout, /change-password
3. web/ Jinja templates: base, login, setup, dashboard, rules (+builder), sources, runs,
   run_detail, settings, field-reference slide-out panel
4. static/app.src.css Tailwind source
5. main.py: register AuthMiddleware, set app.state.sessions, include all routers, mount /static

## Remaining — Phase 7
- Config + rule import/export (YAML/JSON, content-hash gated, imported rules land disabled)
- 5–10 example rules
- Benchmark fixture: 10k torrents x 50 rules, assert well under 1s
- README (setup, config reference, rule/SQL reference, snapshot schema, parallelism map, screenshots)
- .github/workflows: multi-arch amd64+arm64 buildx + GHA cache, provenance + sbom

## Verify after resuming
uv run pytest tests -q      # expect 139+ passing
uv run ruff check src tests

Two things worth flagging before you go: the plan's verification steps (steps 2–9 — first-run wizard, connection tests, live SQL preview, dry-run vs live idempotency, docker compose up) have not been run yet, since the UI and Docker build don't exist in runnable form. And the 10k-torrent benchmark that the spec calls for hasn't been written, so the sub-second claim is currently unproven.
