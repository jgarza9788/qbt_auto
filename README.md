# qbit-flow

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)
![qBittorrent](https://img.shields.io/badge/qBittorrent-WebUI-2F67BA?logo=qbittorrent&logoColor=white)
![License](https://img.shields.io/badge/license-FSL-028ffa)

A self-hosted, Dockerized automation engine for a media + torrent stack.

qbit-flow reads **multiple** Plex, Jellyfin, and qBittorrent instances; scores every torrent for
**watch popularity** from real watch data (normalised per qBittorrent category); and runs your
**rules** to apply **events** — tag, category, move, speed limits, start/stop seeding, export
`.torrent`, run a script. Everything is configured from a web UI; it runs continuously (no cron,
no pipelines — one global rule list on a shared tick).

> Successor to the `qbt_auto` console app. That CLI has been retired; a legacy `config.json` is
> imported on first boot — see [`docs/migration.md`](docs/migration.md).
> How the engine actually works: [`docs/engine.md`](docs/engine.md).

---

## Run it

```bash
docker compose up -d          # UI on http://localhost:8080
```

`docker-compose.yml` mounts `./data` (database + logs), `./config` (optional legacy `config.json`),
`./exports` (`.torrent` output) and `./scripts`. **Bind-mount your media root at the same path
qBittorrent uses** — `torrent.move`, `script.run` and the `<drive>_*` fields need it. A startup
diagnostic warns about save-paths it can't see.

Without Docker:

```bash
ConnectionStrings__Db="Data Source=./data/qbitflow.db" dotnet run --project src/QbitFlow.Web
```

---

## How it works

```
  every N seconds (min 30, "Rule check interval")   separate, slower schedule (default 6h)
  ┌─ RULE ENGINE PASS ─────────────┐    ┌─ ANALYTICS ──────────────────────────┐
  │ 1. refresh each needed qBt      │    │ pull media + watch data from every    │
  │    instance ONCE into a shared  │    │ Plex / Jellyfin source, aggregate a   │
  │    snapshot (skip if fresh);    │    │ recency-weighted watch total per      │
  │    a down source is logged      │    │ media item, match every torrent to a  │
  │ 2. for every torrent, run every │◄───┤ media item, bucket by qBt category,   │
  │    enabled rule in order; fire  │    │ quantile-normalise → watch_popularity │
  │    its event when the criteria  │    │ 0..1 (MediaScoreCache — the ONLY      │
  │    match (log-only under dry-run│    │ thing that touches Plex/Jellyfin)     │
  │    or while in per-rule cooldown│    └──────────────────────────────────────┘
  │ 3. write run summary + log      │
  └────────────────────────────────┘
```

* **Sources** — N Plex, N Jellyfin, N qBittorrent. Each has a health check; secrets are encrypted at
  rest (or overridden by env vars, never persisted).
* **Rules** — one global ordered list on `/Rules`, two sections: the rule table and the rule editor.
  Each rule is a condition (structured builder — field / operator / value, AND / OR — or a raw
  expression) + one event, with optional per-rule qBittorrent-instance filter and a cooldown.
  Helpers `contains`, `match`, `daysAgo`. Fields cover torrent state, per-drive free space, matched
  media metadata, and the derived `<watch_popularity>` (legacy alias `<hotcold>`) / `<watch_total>` /
  `<days_since_last_watched>` / `<is_media_matched>` — the **Fields** slide-over lists every one.
  "Test against current torrents" previews matches without touching anything; nothing is saved until
  you press Save.
* **Engine** — runs continuously. Global toggles live in **Settings**: enabled/paused, dry-run
  default, "CPU speed" (Low / Medium / High / SuperHigh → per-pass parallelism), stop-at-first-match,
  and the **Rule check interval** (also the max age of the shared torrent snapshot). The dashboard
  shows engine state and a **Run now** / **Pause** control.
* **Events** — `tag.sync` / `tag.add` / `tag.remove`, `category.set`, `torrent.move`, `speed.limit`,
  `seeding.start` / `seeding.stop` / `seeding.forceStart`, `script.run`, `torrent.export`.
* **Runs** — full history, per-rule counts, and a live log (SSE) while running. Cancel a run from its
  page.

---

## Configuration

Most settings live in the **Settings** page. Environment variables override where noted.

| Variable | Purpose |
|---|---|
| `ConnectionStrings__Db` | SQLite path (default `data/qbitflow.db`) |
| `CONFIG_IMPORT_PATH` | legacy `config.json` to import on first boot |
| `SECRETS_ENCRYPTION` | `none` (base64, default) or `dpapi` (Data Protection key ring at `SECRETS_KEY_DIR`) |
| `EXPORTS_DIR` | where `torrent.export` writes (default `exports`) |
| `AUTH_MODE` / `AUTH_SECRET` | `none` \| `apikey` (`X-Api-Key` / `?apikey=`) \| `basic`; overrides the Settings page |
| `QBT_URL` / `QBT_USER` / `QBT_PWD`, `PLEX_URL` / `PLEX_TOKEN`, `JELLYFIN_URL` / `JELLYFIN_SECRET`, `SOURCE__<NAME>__{BASEURL,USERNAME,SECRET}` | per-source secret overrides (env always wins, never stored) |

---

## Develop

```bash
dotnet build QbitFlow.sln
dotnet test  QbitFlow.sln
dotnet dotnet-ef migrations add <Name> --project src/QbitFlow.Infrastructure --startup-project src/QbitFlow.Web --output-dir Data/Migrations
```

Solution layout: `QbitFlow.Core` (domain, expressions, abstractions) → `QbitFlow.Infrastructure`
(EF Core / SQLite, config import, secrets) and `QbitFlow.Sources` (Plex / Jellyfin / qBittorrent
adapters) → `QbitFlow.Engine` (`RuleEngine/` loop + runner, actions, analytics) → `QbitFlow.Web`
(ASP.NET Core + Razor Pages + HTMX). Tests in `tests/QbitFlow.Tests`.

## Support
☕ [Buy Me a Coffee](https://buymeacoffee.com/jgarza97885)
