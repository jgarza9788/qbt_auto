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
   See [Setting up sources](#setting-up-sources) — **the name you give each instance
   becomes part of every field key that addresses it**, so pick short ones.
2. **Settings** — set up **path mappings** if your qBittorrent and media-server
   containers mount the same files at different paths. Without them, nothing correlates
   a torrent to its library item, and every cross-source rule matches nothing. Also
   review the global dry-run / kill switch and parallelism level here.
3. **Rules** — add a rule, or import the bundled examples (see
   [Example rules](#example-rules)).

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

## Setting up sources

Everything a rule can look at comes from an **instance** (a connection to one running
service) or a **storage path** (a directory whose disk usage is measured locally).

### Instances

**Instances → Add instance.** Each one needs a name, a source type, a base URL, and
whatever credential that type uses. **Test connection** hits the real API and reports
what it got back before you save.

| Source type | What it contributes | Credential | Sent as |
|---|---|---|---|
| `Qbittorrent` | The torrents rules act on. At least one is required. | username + password | WebUI login, session cookie (omitted entirely if you leave the username blank, for a WebUI with auth bypassed) |
| `Plex` | Library items — what your library knows about a file | API key = your Plex token | `X-Plex-Token` header |
| `Jellyfin` | Library items | API key | `Authorization: MediaBrowser Token="…"` |
| `Tautulli` | Playback events — who watched what, when | API key | `?apikey=` query parameter |
| `Jellystat` | Playback events | API key | `X-Api-Key` header |
| `Jellyglance` | Playback events | API key | `X-Api-Key` header |
| `Streamystats` | Playback events | API key | `X-Api-Key` header |

Credentials are encrypted at rest with ASP.NET Core Data Protection (the key ring lives
beside the database in `QBITFLOW_DATA_DIR`), are decrypted only in memory when an
adapter makes a request, and are never included in a config export. Leaving the
password or API key blank when editing an existing instance keeps the stored one.

You can add **more than one instance of the same type** — two qBittorrent servers, a
Plex and a Jellyfin, whatever — and address each one separately in a rule.

#### Naming rules

An instance's name is the middle segment of every field key that reads its data
(`jellyfin.`**`jellyfin1`**`.title`), so names are validated:

- letters, digits, hyphens and underscores only; must start with a letter or digit
- no dots (the key separator), no `*` (the wildcard), no spaces
- unique across all instances, regardless of type

Renaming an instance later does **not** rewrite rules that reference it — those rules
will fail to compile with an error naming the instances that do exist. The same
constraints apply to storage-path names.

#### Adapting a source whose API doesn't match

Tautulli has a stable documented API and its defaults should just work. Jellystat,
Jellyglance and Streamystats do not, so their shipped endpoint and field mapping are a
best-effort starting point. If yours is shaped differently, override it per-instance in
**Extra config (JSON)** — no code change, no rebuild:

```jsonc
{
  "historyPath": "/api/servers/1/statistics/history",  // appended to the base URL; {apiKey} is substituted
  "resultsPath": "data",                               // dotted path to the array in the response ("" = the root)
  "fieldMap": {                                        // logical name -> the JSON property in your deployment
    "title":     "itemName",
    "filePath":  "filePath",
    "user":      "userName",
    "watchedAt": "startTime",
    "percent":   "percentComplete"
  }
}
```

Only the keys you list are overridden; the rest keep their defaults. `filePath` is the
one that matters most — it is what correlates a playback event back to a torrent.

### Storage paths

**Instances → Add storage path.** A name and a directory. qbitflow reads the
filesystem's capacity/used/free for that path, and optionally its recursive folder size
on an interval you set. These are addressed as `storage.<name>.<field>`, so the name
follows the same naming rules as an instance.

The path must be visible **to the qbitflow container**. If you want to check the disk
your downloads actually live on, mount it (see the commented-out volume lines in
`docker-compose.yml`).

### Path mappings

**This is the setting that makes cross-source rules work.** qbitflow correlates rows
across sources by **normalized file path** — not by title, and not by any shared ID. At
ingest, every path from every source goes through the same normalization, and a torrent
is linked to a library item or a playback event when the results match.

That works only if all your services report the same file under the same path. They
usually don't — qBittorrent might see `/downloads/Movies/Foo.mkv` while Jellyfin sees
`/media/Movies/Foo.mkv`. **Settings → Path mappings** fixes that: map each service's
prefix onto one canonical prefix.

| Source prefix | Canonical prefix |
|---|---|
| `/downloads` | `/media` |

If cross-source rules match nothing and you expected them to, this is almost always
why. Dry-run a rule and compare its matched count against the torrent count in the
snapshot — if the torrent side matches but the correlated side never does, the paths
aren't lining up.

## Rule expression reference

A rule is a schedule, a condition, and one or more actions. The condition is written
either in the **visual builder** (AND/OR groups of comparisons, each picking a source
and a field, plus related-source checks; one level of nested sub-groups) or in
[**advanced SQL**](#advanced-sql). Both address data the same way.

### Field keys

Every piece of source data is addressed the same way:

```
<type>.<instance>.<field>
```

| Segment | What it is |
|---|---|
| `<type>` | `qbittorrent`, `plex`, `jellyfin`, `tautulli`, `jellystat`, `jellyglance`, `streamystats`, or `storage` |
| `<instance>` | the name you gave that instance (or storage path), or `*` for any instance of that type |
| `<field>` | one of the fields that type exposes — see [the catalog below](#field-catalog) |

```
jellyfin.jellyfin1.title          the title, as reported by the Jellyfin server named "jellyfin1"
qbittorrent.*.category            the category, on any of your qBittorrent instances
qbittorrent.seedbox.ratio         the ratio, but only for torrents on the instance named "seedbox"
tautulli.*.play_count             how many times this torrent's content was played, per Tautulli
storage.downloads.used_percent    how full the storage path named "downloads" is
```

The same grammar is used everywhere — the visual builder, inside a related-source
check, and in advanced SQL — so a key copied out of the **Field reference** panel
pastes into any of them.

#### Wildcards

`*` in the instance segment means "any instance of this type", and compiles to the same
query with the instance filter left out. It is what you want in almost every rule, and
it is what the bundled examples use, since they ship to installs whose instance names
can't be known in advance.

Name a specific instance when you actually mean one: `qbittorrent.seedbox.ratio > 2`
matches only torrents on `seedbox`, and `jellystat.main.play_count = 0` asks about one
particular Jellystat server rather than pooling every history source you run.

There is no wildcard for the *type* segment — `*.*.title` is not a thing. Ask about
each source you care about, or use an OR group.

#### How a key resolves

The three source shapes compile differently, and the difference is visible in the
rule editor's **Preview SQL**:

- **`qbittorrent.<instance>.<field>`** reads the torrent row itself. Naming an instance
  also restricts *which* torrents match, so `qbittorrent.seedbox.progress >= 1` means
  "finished, and on seedbox".
- **A media/history row field** (`jellyfin.jf1.title`, `tautulli.*.user_name`) is
  correlated to the torrent for you: it becomes `EXISTS (… WHERE path_key = <the
  torrent's> …)`. So `tautulli.*.days_since_watched <= 90` at the top level reads as
  "this torrent's content was watched in the last 90 days".
- **A media/history aggregate** (`play_count`, `last_watched_at`, `media_count`, …)
  spans *every* matching row at once and becomes a scalar subquery, so it compares
  directly. `tautulli.*.play_count = 0` is "never watched" — `COUNT(*)` is `0`, not
  NULL, when nothing matches. The timestamp aggregates *are* NULL when nothing matches,
  so `days_since_last_watched > 180` correctly excludes never-watched content rather
  than including it.
- **`storage.<name>.<field>`** isn't tied to a torrent at all. It's a global check, so
  it either lets the whole rule through or blocks it.

#### Related-source checks

A single comparison on another source doesn't need one — the correlation above is
automatic. What a related-source check (EXISTS / NOT EXISTS) adds is that several
conditions must hold **on the same row**:

```
watched by alice AND in the last 7 days
```

as one related-source check means *one* playback event that was both. As two separate
top-level comparisons it means alice watched it at some point, and somebody watched it
recently — not the same question.

Use the **There is / There is no &lt;source&gt;** row in the builder, pick the source it
looks at, and the fields inside it are restricted to that source's per-row fields.

### Worked example

Say you run one qBittorrent (`qbt1`), one Jellyfin (`jf1`), one Tautulli (`taut1`), and
a storage path named `downloads`. You want: **tag finished torrents that nobody has
watched in six months, so you can review them for deletion.**

In the visual builder that is three conditions, ALL of:

| Source | Field | Operator | Value |
|---|---|---|---|
| `qbittorrent.*` | `progress` | ≥ | `1` |
| `qbittorrent.*` | `days_since_added` | > | `30` |
| `tautulli.*` | `days_since_last_watched` | > | `180` |

with one action, **add tag** `review-for-deletion`.

Two things to notice:

- The third row reads a **Tautulli** field on a rule that returns **torrents**. You did
  not have to join anything — a key naming another source is correlated to the torrent
  by file path automatically.
- `days_since_last_watched` is an aggregate, so it looks at every playback event for
  that torrent's content and takes the most recent. It is NULL when there are none, so
  a torrent nobody has *ever* watched does **not** match this rule. If you want those
  too, add an OR group with `tautulli.*.play_count = 0`.

Hit **Preview SQL** to see exactly what that compiles to, and **Dry run** to see what it
matches against live data without changing anything. The dry run reports both the
matched count and the total torrents in the snapshot — if the total is 0, the problem is
the qBittorrent connection; if the total is right but nothing matches, it is usually
[path mappings](#path-mappings).

Rules stay in dry-run until you clear the per-rule **Dry-run** checkbox *and* the global
one in Settings.

### Field catalog

The **Field reference** panel in the rule editor is the live version of this — it is
built from the same catalog plus your configured instances, so it always shows real,
paste-ready keys. The tables below are the same content for offline reading.

#### `qbittorrent`

Prefix each with `qbittorrent.<instance>.` or `qbittorrent.*.`.

| Field | Type | Meaning | Example |
|---|---|---|---|
| `name` | Text | The torrent's display name. | `Ubuntu 24.04 ISO` |
| `category` | Text | qBittorrent category, if assigned. | `linux` |
| `tags` | Text | Comma-separated tag list; use Contains to match one tag. | `iso,verified` |
| `state` | Text | qBittorrent status string. | `uploading` |
| `save_path` | Text | Torrent's save location. | `/downloads` |
| `content_path` | Text | Torrent's content path (file or folder). | `/downloads/Ubuntu.iso` |
| `size_bytes` | Integer | Total size in bytes. | `5368709120` |
| `size_gb` | Real | Total size in GB. | `5.37` |
| `progress` | Real | Download progress, 0..1. | `1.0` |
| `ratio` | Real | Upload/download ratio. | `0.42` |
| `downloaded_bytes` | Integer | Bytes downloaded. | `5368709120` |
| `uploaded_bytes` | Integer | Bytes uploaded. | `2147483648` |
| `upload_limit_bps` | Integer | Upload speed limit, bytes/sec (0 = unlimited). | `0` |
| `download_limit_bps` | Integer | Download speed limit, bytes/sec (0 = unlimited). | `0` |
| `added_on` | DateTime | When the torrent was added. | `2026-01-01T00:00:00+00:00` |
| `days_since_added` | Real | Days since the torrent was added. | `42.5` |
| `completion_on` | DateTime | When the torrent finished downloading. | `2026-01-02T00:00:00+00:00` |
| `days_since_completed` | Real | Days since the torrent finished downloading. | `41.5` |
| `tracker` | Text | Currently-working tracker URL (empty when none is working). | `udp://tracker.example.org:451/announce` |
| `total_size_bytes` | Integer | Size of all selected files in bytes (>= size_bytes). | `8561604253` |
| `total_size_gb` | Real | Size of all selected files in GB. | `8.56` |
| `amount_left_bytes` | Integer | Bytes still to download (0 once complete). | `0` |
| `amount_left_gb` | Real | GB still to download. | `0.0` |
| `completed_bytes` | Integer | Bytes of selected content already downloaded. | `8561604253` |
| `download_speed_bps` | Integer | Current download rate, bytes/sec. | `0` |
| `upload_speed_bps` | Integer | Current upload rate, bytes/sec. | `1048576` |
| `eta_seconds` | Integer | Estimated seconds to completion (8640000 means qBittorrent reports no ETA). | `8640000` |
| `eta_hours` | Real | Estimated hours to completion. | `2400.0` |
| `seeding_time_seconds` | Integer | Seconds spent seeding. | `62777820` |
| `seeding_days` | Real | Days spent seeding. | `726.6` |
| `active_time_seconds` | Integer | Seconds the torrent has been active (downloading or seeding). | `62828030` |
| `active_days` | Real | Days the torrent has been active. | `727.2` |
| `connected_seeds` | Integer | Seeds currently connected to. | `3` |
| `total_seeds` | Integer | Seeds in the swarm reported by the tracker. | `12` |
| `connected_leechers` | Integer | Leechers currently connected to. | `1` |
| `total_leechers` | Integer | Leechers in the swarm reported by the tracker. | `50` |
| `availability` | Real | Fraction of the torrent available across peers; -1 when unknown. | `1.0` |
| `auto_tmm` | Boolean | Whether Automatic Torrent Management is enabled for this torrent. | `true` |
| `ratio_limit` | Real | Per-torrent share-ratio limit: -2 = use global, -1 = unlimited. | `-2.0` |
| `seeding_time_limit_minutes` | Integer | Per-torrent seeding-time limit in minutes: -2 = use global, -1 = unlimited. | `-2` |
| `last_activity` | DateTime | When the torrent last had tracker/peer activity. | `2026-07-27T06:44:34+00:00` |
| `days_since_activity` | Real | Days since the torrent last had activity. | `40.3` |
| `seen_complete` | DateTime | When a complete copy was last seen in the swarm. | `2026-07-27T06:44:34+00:00` |
| `days_since_seen_complete` | Real | Days since a complete copy was last seen in the swarm. | `40.3` |

#### `plex`, `jellyfin`, `tautulli`, `jellystat`, `jellyglance`, `streamystats`

These six share one vocabulary, because they all describe the same two kinds of row: a
**library item** (your media server knows about this file) and a **playback event**
(somebody watched it). Which of them a given source actually reports is up to that
source — today Plex and Jellyfin report library items, and Tautulli, Jellystat,
Jellyglance and Streamystats report playback events. A field a source never reports
simply reads NULL rather than being an error.

Per-row fields — usable at the top level (auto-correlated) or inside a related-source
check:

| Field | Type | Applies to | Meaning | Example |
|---|---|---|---|---|
| `kind` | Text | all rows | Which kind of row this is: 'media' (a library item) or 'history' (a playback event). | `history` |
| `title` | Text | all rows | Item title, as reported by the source. | `Foo (2020)` |
| `file_path` | Text | all rows | File path the source reported for this row. | `/media/movies/Foo.mkv` |
| `media_type` | Text | library item | movie, episode, etc. as reported by the source. | `movie` |
| `external_key` | Text | library item | The source's own id for the library item. | `12345` |
| `added_at` | DateTime | library item | When the media library added this item. | `2026-01-01T00:00:00+00:00` |
| `days_since_added` | Real | library item | Days since the media library added this item. | `42.5` |
| `user_name` | Text | playback event | Viewer's username. | `alice` |
| `percent_complete` | Real | playback event | Percent of the item watched, 0..100. | `95.0` |
| `watched_at` | DateTime | playback event | When this watch event occurred. | `2026-01-01T00:00:00+00:00` |
| `days_since_watched` | Real | playback event | Days since this watch event. | `10.2` |

Aggregates — these span every row matching the torrent, so they are used at the top
level and not inside a related-source check:

| Field | Type | Meaning | Example |
|---|---|---|---|
| `play_count` | Integer | How many times this torrent's content was watched. 0 when never watched. | `3` |
| `distinct_viewers` | Integer | How many different users watched it. | `2` |
| `first_watched_at` | DateTime | Earliest watch event. NULL when never watched. | `2025-06-01T00:00:00+00:00` |
| `last_watched_at` | DateTime | Most recent watch event. NULL when never watched. | `2026-01-01T00:00:00+00:00` |
| `days_since_last_watched` | Real | Days since the most recent watch event. NULL when never watched. | `10.2` |
| `media_count` | Integer | How many library items point at this torrent's content. 0 when the library doesn't have it. | `1` |

#### `storage`

Prefix each with `storage.<name>.`, using a storage path's configured name. `*` works
in the visual builder ("any configured path"), but advanced SQL requires a name.

| Field | Type | Meaning | Example |
|---|---|---|---|
| `total_bytes` | Integer | Total capacity of the filesystem, in bytes. | `2000398934016` |
| `used_bytes` | Integer | Bytes in use. | `1500299200512` |
| `free_bytes` | Integer | Bytes free. | `500099733504` |
| `used_percent` | Real | Percent of capacity in use, 0..100. | `75.0` |
| `free_percent` | Real | Percent of capacity free, 0..100. | `25.0` |
| `free_gb` | Real | Free space in GB. | `500.1` |
| `used_gb` | Real | Used space in GB. | `1500.3` |
| `folder_size_gb` | Real | Recursive size of the configured folder in GB, if scanned. | `820.4` |
| `available` | Boolean | False when the path doesn't exist, isn't mounted, or couldn't be read. | `true` |

#### Helper functions

Callable from advanced SQL, and what the computed fields above use internally:

| Function | Returns |
|---|---|
| `days_since(timestamp)` | Days between now and an ISO-8601 timestamp. NULL if the timestamp is NULL. |
| `size_gb(bytes)` | A byte count in gigabytes (decimal, 1e9). NULL if bytes is NULL. |
| `path_matches(a, b)` | True if two normalized paths are equal, or one contains the other. |

### Advanced SQL

Switch a rule to advanced mode when the visual builder can't say what you mean — deeper
nesting, a multi-condition EXISTS body, arithmetic across fields. You write a boolean
WHERE-clause expression; qbitflow wraps it in
`SELECT … FROM qbittorrent t WHERE <yours>` and the torrent row is aliased `t`.

Field keys work verbatim, and are rewritten into the same SQL the visual builder
compiles to:

```sql
qbittorrent.*.active_days >= 14
  AND storage.downloads.used_percent < 90
  AND streamystats.*.play_count = 0
```

Specifics worth knowing:

- **`qbittorrent.<name>.<field>`** becomes `CASE WHEN t.instance = '<name>' THEN … END`,
  so it is NULL — and therefore false in any comparison — on torrents from other
  instances. That keeps it composable inside whatever expression you write around it.
- **A per-row field of a related source is rejected**, e.g.
  `tautulli.*.watched_at > '2026-01-01'`. There are many playback events per torrent, so
  it has no single value here; the error tells you which aggregates to use instead. If
  you want the row-level question, write your own `EXISTS (SELECT 1 FROM tautulli …)` —
  the per-type tables are named exactly like the type segment.
- **`storage.*` is rejected** in SQL mode for the same reason — name a path, or write
  your own EXISTS over the `storage` table.
- Keys inside string literals, quoted identifiers and comments are left alone, as are
  your own aliases (`t.category`) and anything that isn't a known source type.

It runs on a `PRAGMA query_only` connection, single-statement only, with a keyword
denylist, a row cap and a timeout — and it is validated with `EXPLAIN QUERY PLAN`
before you can save it, so a broken query is caught at save time rather than at the
next scheduled run.

### Actions

Add tag(s), remove tag(s), set category, move (with optional
wait-for-completion verification), set upload limit, set download limit. Every action
applies to the torrents a rule matched, so a rule always resolves to a set of torrents
no matter how many sources its condition consulted. All actions
are idempotent — a torrent already in the desired state is skipped, not reapplied —
and batched per instance (one API call covers every matched torrent on that
instance, not one call per torrent).

### Scheduling

Standard 5-field cron, a human-friendly preset picker, a live English
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
- **Qbitflow.Snapshot** — the in-memory SQLite database rebuilt each rule run (see
  [The snapshot schema](#the-snapshot-schema)), the path normalizer, and the SQLite
  UDFs.
- **Qbitflow.Engine** — the condition-tree → parameterized-SQL compiler, the advanced
  SQL validator/executor, the action executor, cron scheduling, and RuleRunner (the
  glue that ties a rule's run together end to end).
- **Qbitflow.Infrastructure** — EF Core persistence (config, instances, rules, run
  history), auth, config/rule import-export.
- **Qbitflow.Web** — ASP.NET Core Razor Pages + HTMX + Alpine.js UI.
- **Qbitflow.Tests** — xUnit: the SQL compiler, snapshot schema, cron handling, action
  idempotency, the example rule library, and a 10,000-torrent/50-rule benchmark.

### The snapshot schema

Rules never query your services directly. Each run refreshes the enabled instances
(through a per-source TTL cache, so a rule that just ran doesn't re-fetch), then builds
a fresh **in-memory SQLite database** and evaluates against that. It is discarded when
the run ends — nothing about it is persisted.

The schema is addressed by source, which is what makes a field key resolve directly:
`jellyfin.jellyfin1.title` is the `title` column of the `jellyfin` table where
`instance = 'jellyfin1'`.

| Table | Rows | Notable columns |
|---|---|---|
| `qbittorrent` | one per torrent | `instance_id`, `instance`, `hash`, `path_key`, + every torrent field |
| `plex`, `jellyfin`, `tautulli`, `jellystat`, `jellyglance`, `streamystats` | one per library item or playback event | `instance`, `kind` (`media` / `history`), `title`, `file_path`, `path_key`, `added_at`, `user_name`, `watched_at`, `percent_complete` |
| `storage` | one per configured storage path | `instance`, `path`, `total_bytes`, `used_bytes`, `free_bytes`, `used_percent`, `folder_size_bytes` |
| `qbittorrent_files` | one per file in a torrent | not populated yet; see `docs/IMPROVEMENTS.md` |

The six media/history tables share one wide shape with a `kind` discriminator, so a
source that reports both library items and playback events needs no schema change —
the columns that don't apply to a row are just NULL. The whole DDL is generated by
looping over the `SourceType` enum, so **adding a source type means adding an enum
value and an adapter, and nothing else**.

`path_key` is the normalized file path, computed once at ingest with your configured
path mappings applied, and is what every cross-source correlation joins on.

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

### Adding a source type

The per-type schema and the field catalog are both generated from the `SourceType`
enum, so a new media/history source is a small, well-defined change:

1. Add the value to `SourceType` (`src/Qbitflow.Core/Domain/Enums.cs`). That alone
   creates its snapshot table, its catalog entry, and its option in the instance
   editor's source dropdown.
2. Add an adapter in `src/Qbitflow.Sources/Adapters/`. A REST watch-history source can
   usually derive from `RestHistoryAdapterBase` and supply three defaults — see
   `StreamystatsAdapter.cs`, which is ~35 lines.
3. Register it in `ServiceCollectionExtensions.cs` and give it a TTL in
   `SourceCacheOptions.cs`.
4. Have it stamp `SourceType` on the records it emits — that is what routes each row to
   its own table.

`SourceFieldCatalogTests` will then check the new type's fields actually compile and
execute against the real schema, and that it shares the media/history vocabulary.

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
