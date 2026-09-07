# qbitflow

Rule-driven automation for qBittorrent, informed by your media library and watch
history. Define rules — a schedule, a condition, and one or more actions — and
qbitflow evaluates them against a live snapshot of your qBittorrent, Plex, Jellyfin,
Tautulli, Jellystat, Jellyglance, Streamystats, and disk-usage data, then tags,
re-categorizes, moves, or throttles matching torrents.

Self-hosted, single Docker image, SQLite only (no external database, no Redis).

## Quick start

```bash
git clone <this repo>
cd qbitflow
docker compose up -d
```

Open `http://<host>:8080`. The first visit takes you through a setup wizard to create
the admin account. From there:

1. **Instances** — add your qBittorrent instance (required) and, optionally, Plex,
   Jellyfin, Tautulli, Jellystat, Jellyglance, Streamystats, and named storage paths.
2. **Rules** — add a rule, or import the bundled examples (see below).
3. **Settings** — review the global dry-run/kill-switch, parallelism level, and path
   mappings if your qBittorrent and media-server containers mount the same files at
   different paths.

Every example rule and anything you write yourself should be reviewed with **dry-run
on** before you trust it against real data.

## Configuration reference

All persistent state (SQLite database + the data-protection key ring used to encrypt
instance credentials at rest) lives under one directory, configurable via:

| Env var | Default | Purpose |
|---|---|---|
| `QBITFLOW_DATA_DIR` | `/data` in the container, `./data` when run locally | SQLite DB + key ring |
| `QBITFLOW_LOG_DIR` | `/log` in the container, `./log` when run locally | Rolling log files (see [Troubleshooting](#troubleshooting)) |
| `QBITFLOW_LOG_LEVEL` | _(unset)_ | Overrides the Settings-page log level when set (`Trace`/`Debug`/`Information`/`Warning`/`Error`/`Critical`) |

Everything else (parallelism level, dry-run, kill switch, theme, log level, timezone,
path mappings) is configured from the **Settings** page, not environment variables —
this is what gets backed up when you export config, and what gets applied without a
restart. The log level now applies immediately on save too.

`docker-compose.yml` mounts `${QBITFLOW_LOCATION}/data` at `/data` (your entire
configuration, rules, and run history — back this up) and `${QBITFLOW_LOCATION}/log`
at `/log` (the rolling log files, safe to discard).

If you use the **move** action and want qbitflow to verify the move against the same
paths your qBittorrent/Plex/Jellyfin containers see, mount those paths into the
qbitflow container too (see the commented-out volume lines in `docker-compose.yml`)
and set up a path mapping in Settings if the mount points differ between containers.

## Rule expression reference

A rule's condition is either:

- **Visual builder** (default): AND/OR groups of comparisons, each picking a source
  (`<type>.<instance>`) and a field, plus "there is / is no &lt;source&gt; row
  matching..." related-source checks (EXISTS / NOT EXISTS). Supports one level of
  nested sub-groups.
- **Advanced SQL**: a raw boolean WHERE-clause expression against the documented
  snapshot schema, for anything the visual builder can't express (deeper nesting,
  multi-condition EXISTS bodies). Runs read-only, single-statement only, validated
  with `EXPLAIN QUERY PLAN` before you can save it.

Click **Field reference** on the rule editor for the full, always-current list of
keys, their types, descriptions, and example values — it's built from the field
catalog and your configured instances, not hand-maintained, so it never drifts out of
date.

### Field keys

Every piece of source data is addressed the same way:

```
<type>.<instance>.<field>
```

- **`<type>`** — `qbittorrent`, `plex`, `jellyfin`, `tautulli`, `jellystat`,
  `jellyglance`, `streamystats`, or `storage`.
- **`<instance>`** — the name you gave that instance under **Instances** (or the name
  of a configured storage path), or `*` for any instance of that type.
- **`<field>`** — one of the fields that type exposes.

So `jellyfin.jellyfin1.title` is the title as reported by the Jellyfin instance you
named `jellyfin1`, and `qbittorrent.*.category` is the category on any of your
qBittorrent instances. Because the instance name is a segment of the key, instance and
storage-path names are restricted to letters, digits, hyphens and underscores.

The fields per type:

- **qbittorrent** — name, category, tags, save_path, content_path, size_gb, progress,
  ratio, state, days_since_added, days_since_completed, upload/download limits, etc.
  Naming an instance (`qbittorrent.qbt1.category`) also restricts which torrents match.
- **plex / jellyfin / tautulli / jellystat / jellyglance / streamystats** — one shared
  vocabulary, since these all describe the same two kinds of row. Library items:
  title, media_type, external_key, file_path, added_at, days_since_added. Playback
  events: title, user_name, watched_at, days_since_watched, percent_complete. Plus
  aggregates over everything matching the torrent: play_count, distinct_viewers,
  first_watched_at, last_watched_at, days_since_last_watched, media_count. A field a
  given source never reports simply reads NULL.
- **storage** — used_percent, free_percent, free_gb, used_gb, total_bytes,
  folder_size_gb, available, for each named storage path you've configured.

A comparison against a non-qBittorrent source is correlated to the torrent for you (by
normalized file path), so `tautulli.*.days_since_watched <= 90` at the top level means
"this torrent's content was watched in the last 90 days". A **related-source check**
(EXISTS / NOT EXISTS) is only needed when several conditions have to hold on the *same*
row — "watched by alice **and** in the last 7 days", as opposed to watched by alice at
some point and watched recently by anyone.

Three helper functions are callable from advanced SQL (and are what the computed
fields above use internally): `days_since(timestamp)`, `size_gb(bytes)`,
`path_matches(a, b)`.

**Actions**: add tag(s), remove tag(s), set category, move (with optional
wait-for-completion verification), set upload limit, set download limit. All actions
are idempotent — a torrent already in the desired state is skipped, not reapplied —
and batched per instance (one API call covers every matched torrent on that
instance, not one call per torrent).

**Scheduling**: standard 5-field cron, a human-friendly preset picker, a live English
description and next-3-runs preview, and a hard 5-minute minimum interval (faster
schedules are rejected with an explanation, not silently sped up or slowed down). A
rule can never start again while its previous run is still in flight.

### Example rules

`examples/example-rules.json` has 9 ready-to-import rules covering the patterns above
(tag unwatched media, remove a tag once rewatched, throttle a category, move
completed-and-unwatched torrents to cold storage, flag large torrents, flag low disk
space, remove an upload cap for a priority category, flag old never-watched torrents,
flag torrents missing from the media library). Import it from **Settings → Config
import / export → Import → Rules**. They address `tautulli.*` / `jellyfin.*` because
they ship to installs whose instance names can't be known in advance — change the type
segment to whichever media/history source you actually run.
Every example ships with dry-run on — review what it matches before disabling that.

## Breaking change: field keys

Rules written before source data moved to `<type>.<instance>.<field>` do not load. A
condition using a bare key (`category`, `days_since_watched`) or an EXISTS node's old
`Relation` property fails at compile time with an error naming the offending key, and
the same applies to a rules export taken from an older build. There is no automatic
migration — rewrite the affected rules in the editor, where the field picker now
offers a source and a field.

What changed, concretely:

- `category` → `qbittorrent.*.category`
- EXISTS `Relation: "watch_history"` → `Source: "tautulli.*"` (or whichever history
  source you run), with the fields inside it fully qualified too
- the `play_counts` relation → aggregate fields, e.g. `tautulli.*.play_count = 0`
- advanced SQL referencing `torrents`, `media_items`, `watch_history` or `play_counts`
  by table name → the per-type tables (`qbittorrent`, `jellyfin`, ...)
- instance and storage-path names containing `.`, `*` or spaces must be renamed, since
  the name is a segment of every key that addresses it

## Troubleshooting

**Logs.** qbitflow writes a rolling file per day to `/log` inside the container
(`qbitflow-YYYY-MM-DD.log`, the last 7 days are kept). With the default compose file
that's `${QBITFLOW_LOCATION}/log/` on the host, so you can read it directly:

```bash
tail -f "${QBITFLOW_LOCATION}/log/qbitflow-$(date +%F).log"
# or, from inside the container:
docker compose exec qbitflow sh -c 'tail -f /log/qbitflow-$(date +%F).log'
```

The same lines also go to stdout as JSON: `docker compose logs -f qbitflow`.

**Log level.** Set it on the **Settings** page (`Information` by default) — it takes
effect immediately, no restart. `QBITFLOW_LOG_LEVEL` overrides it if you need to set a
level before the app can reach its database.

**qBittorrent connection test fails.**

- *"qBittorrent rejected the login …"* — the WebUI credentials are wrong, **or**
  qBittorrent has temporarily banned qbitflow's IP after repeated failed logins
  (Options → Web UI → "Ban client after consecutive failures", default 5 / 3600 s).
  Restart qBittorrent or wait out the ban, then retry. Newer qBittorrent generates a
  random admin password on first run (printed to its own log) — `adminadmin` won't work.
- *"qBittorrent auth returned HTTP 403 / 401"* — usually qBittorrent's host-header
  validation rejecting the request, or the WebUI not actually listening where you think.
- *"Could not reach qBittorrent at …"* — DNS/networking; from inside the qbitflow
  container the qBittorrent URL must be resolvable (use the compose service name or a
  reachable IP, not `localhost`).

The connection test and its failure reason are logged at `INFO` / `WARN`, so the log
file above will show exactly what qBittorrent returned. qBittorrent's own log (WebUI →
Tools → Log) is the authoritative source for a rejected login.

## Architecture

- **Qbitflow.Core** — domain models, the condition-tree and action types, and the
  interfaces adapters/executors implement.
- **Qbitflow.Sources** — one adapter per data source (qBittorrent, Plex, Jellyfin,
  Tautulli, Jellystat, Jellyglance, Streamystats) plus the storage-usage service, the
  shared per-instance TTL cache, and the per-host concurrency limiter.
- **Qbitflow.Snapshot** — the in-memory SQLite database rebuilt each rule run: one
  table per source type (`qbittorrent`, `jellyfin`, `tautulli`, ... plus `storage`),
  path_key computed once at ingest, and the SQLite UDFs. The DDL is generated from the
  `SourceType` enum, so a new source type needs an enum value and an adapter and
  nothing here.
- **Qbitflow.Engine** — the condition-tree → parameterized-SQL compiler, the advanced
  SQL validator/executor, the action executor, cron scheduling, and RuleRunner (the
  glue that ties a rule's run together end to end).
- **Qbitflow.Infrastructure** — EF Core persistence (config, instances, rules, run
  history), auth, config/rule import-export.
- **Qbitflow.Web** — ASP.NET Core Razor Pages + HTMX + Alpine.js UI.
- **Qbitflow.Tests** — xUnit: the SQL compiler, snapshot schema, cron handling, action
  idempotency, the example rule library, and a 10,000-torrent/50-rule benchmark.

### Design decisions worth knowing about

- **One snapshot per rule run, not one per cycle.** Different rules have different
  schedules; each run refreshes (via the shared cache, so a rule that just fired
  moments ago for another rule is a cache hit, not a re-fetch), rebuilds its own
  snapshot, and evaluates against it. All actions within *that* run see a consistent
  view.
- **Credentials are encrypted at rest** (ASP.NET Core Data Protection) and never
  appear in a config export.
- **EXISTS correlation uses plain path_key equality**, not the `path_matches` UDF —
  both sides are normalized identically at ingest, and a UDF-based join forces SQLite
  into a row-by-row managed callback instead of an index seek. This one line is the
  difference between the benchmark passing in under a second and taking 34.
- **Jellystat, Jellyglance and Streamystats adapters are config-driven, not hardcoded**
  — none has a single stable public API at the time of writing, so their default
  endpoint/field-mapping is a best-effort starting point, overridable per-instance
  via `ExtraConfigJson` without a code change.
- **Source data is stored by source, not by shape.** Each source type gets its own
  snapshot table with an `instance` column, which is what makes
  `<type>.<instance>.<field>` resolve directly. The alternative — pooling every media
  server into one `media_items` table — made "the title from jellyfin1" impossible to
  ask and limited each source to fields that fit the shared shape.
- **Aggregates replace the old play_counts view.** `play_count`, `last_watched_at` and
  friends are per-type, per-instance fields compiled to a correlated scalar subquery,
  so "never watched" is `tautulli.*.play_count = 0` rather than a negated EXISTS, and
  it can be scoped to one history source instead of silently pooling all of them.

## Development

```bash
dotnet build Qbitflow.sln
dotnet test src/Qbitflow.Tests/Qbitflow.Tests.csproj
```

EF Core migrations live in `src/Qbitflow.Infrastructure/Persistence/Migrations`; add
a new one with:

```bash
dotnet ef migrations add <Name> \
  --project src/Qbitflow.Infrastructure/Qbitflow.Infrastructure.csproj \
  --startup-project src/Qbitflow.Web/Qbitflow.Web.csproj \
  --output-dir Persistence/Migrations
```

## Screenshots

_TODO: add screenshots of the Dashboard, Rules editor (visual builder + field
reference panel), and Run history once the app has been run against a real qBittorrent
instance._

## What's next

- A real browser walkthrough of the UI (it's been verified via automated HTTP
  round-trips through every page and form, including the visual condition/action
  builders' exact JSON output, but not visually in an actual browser).
- Explicit per-rule dataset declarations, so a refresh only touches the sources a
  given rule's condition actually references, rather than refreshing every enabled
  instance on every run.
- Confirm the Jellystat/Jellyglance/Streamystats default endpoint shapes against real
  deployments.
- A live Docker build/run verification (this environment has no Docker CLI available,
  so the Dockerfile has been reviewed but not build-tested).
