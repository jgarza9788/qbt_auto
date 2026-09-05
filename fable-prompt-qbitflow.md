# One-Shot Prompt — qbitflow

Build a complete, production-quality, self-hosted application called **qbitflow**. Deliver the full repo — code, Dockerfile, docker-compose, README — not a plan or a sketch. Make reasonable decisions on your own and state them at the end instead of asking me questions. Use `qbitflow` consistently as the project name, package name, image name, and container name.

## Inspiration / prior art

Read these two branches before writing anything and reuse what's good, especially the auth system, config handling, and qBittorrent client wrapper from Dev8:

- https://github.com/jgarza9788/qbt_auto/tree/Dev8 (primary reference — auth pattern here is acceptable as-is)
- https://github.com/jgarza9788/qbt_auto/tree/Dev7 (earlier logic, useful for rule/action ideas)

Improve on them. Do not copy their limitations.

## What it does

Collect data from multiple media/download services, evaluate user-defined rules against that data on a schedule, and perform actions in one or more qBittorrent instances when a rule evaluates true.

### Data sources (all must support MULTIPLE named instances)

- Plex
- Jellyfin
- qBittorrent
- Jellystat
- Jellyglance
- Tautulli
- **Storage / disk usage** — user-defined list of named paths (volume, drive, or folder). For each: total, used, and free space in GB, plus used and free as a percentage. Also support folder-level size (recursive, cached, with a configurable scan interval since it's expensive). These must be usable in rule conditions, e.g. `storage["downloads"].used_percent > 85` or `storage["media"].free_gb < 200`. Handle paths that don't exist or aren't mounted without crashing the run.

Each instance is configured with: name, base URL, credentials/API key, enabled flag, timeout, verify-SSL flag. Every source is optional — the app must run fine with only qBittorrent configured. Use a provider/adapter interface so new sources can be added by dropping in one file.

### AutoRule

* an AutoRule is a cron schedule, a query, and an action to perform, and a list of qbittorrent instances for those actions to work on.


### Flow

1. when a cron rule is true, hydrate the datasources (if needed i.e not stale ) ( not all datasources will be used by all the rules, just hydrate the needed ones ) ...  data stored in SQLite memory tables
2. run the query and collect the items 
3. for each item perform the action
4. log 1-4 to be easily found


### Shared data layer (important)

- One fetch per source per refresh cycle, shared across ALL rules — never let N rules cause N API calls.
- In-memory cache with per-source TTL, plus explicit invalidation and a "refresh now" action.
- A rule declares which datasets it needs; the scheduler resolves the union of needs, refreshes only what is stale, then evaluates.
- Normalize source data into a stable internal schema (torrents, torrent_files, media_items, watch_history, play_counts, storage_paths) so rules aren't written against raw vendor JSON. Include a path-matching layer that links a qBittorrent torrent's files to Plex/Jellyfin library items (handle differing container mount paths via configurable path mappings). Store a normalized `path_key` column at ingest time — do the messy path normalization once, in C++/C#, not repeatedly at query time.
- **Materialize each refresh into an in-memory SQLite snapshot database** (`:memory:` or tmpfs), with indexes on the join and filter columns. Rebuild or upsert it on refresh; all rules in a cycle evaluate against the same immutable snapshot so results are consistent. Persist only config, rules, and run history to the on-disk SQLite DB.
- Concurrent fetches, per-source failure isolation: one dead service must not break the run.

### Rules

- Each rule: name, description, enabled, target instance(s), condition, actions, schedule, dry-run flag.
- **Conditions compile to SQL and run as set-based queries against the snapshot DB.** A rule's condition is stored as a structured condition tree (JSON), and the engine compiles it to a parameterized `SELECT torrent_id FROM torrents ... WHERE ...` — never string-concatenated user input, never `eval` of C++/C#. The matched set comes back in one query instead of looping over every torrent in C++/C#.
- Support AND/OR/NOT, nested groups, comparisons, dates/durations, string ops, `IN`, `LIKE`, and aggregate/EXISTS subqueries over related sources (e.g. "no watch history in the last 90 days across any Tautulli or Jellystat instance").
- Register C++/C# helpers as SQLite user-defined functions (`create_function`) so things like `days_since(...)`, `size_gb(...)`, `path_matches(...)` are callable from within the compiled SQL. Prefer precomputed columns over UDFs in hot paths — a UDF forces a row-by-row callback.
- **Advanced mode**: let power users write a raw SQL `WHERE` clause (or a full query returning `torrent_id`) against the documented snapshot schema. Execute it on a read-only connection with `authorizer` restrictions, a statement timeout, and a row limit; validate with `EXPLAIN` before saving. Keep the visual condition builder as the default path for everyone else.
- The visual builder, the SQL preview, and the stored condition tree must stay in sync — show users the generated SQL read-only so the abstraction is inspectable.
- Provide BOTH a plain-text expression editor with autocomplete/validation and a visual condition builder for non-technical users.
- Rules evaluate per-torrent and produce a list of matched torrents.
- Rule ordering / priority, and an option to stop processing further rules for a torrent once matched.

### Actions (executed against one or many qBittorrent instances)

- Add tag(s) / remove tag(s)
- Set or change category
- Change save location (move torrent data, with option to wait for/verify the move)
- Set upload speed limit
- Set download speed limit

Actions must be batched where the qBittorrent API allows, idempotent, and safely skipped when the desired state already matches. Include a global kill switch and a global dry-run mode.

### Scheduling

- Per-rule cron expression, PLUS a human-friendly picker ("every 15 minutes", "daily at 3am", "every Sunday"). Convert cron → readable English and back, and show the next 3 run times.
- Enforce a minimum interval of 5 minutes per rule; reject or clamp anything faster and explain why in the UI.
- Overlap protection: a rule cannot start again while its previous run is in flight.
- Timezone-aware.

### Parallel execution

- Global setting: **Low / Medium / High / Very High**, mapped to concrete worker/connection counts (document the mapping, e.g. Low = 2, Medium = 4, High = 8, Very High = 16).
- Applies to both source fetching and action execution.
- Per-host rate limiting / connection caps so a Raspberry Pi or a small NAS can't hammer itself into oblivion.

## Requirements

- **Docker**: single lightweight image (slim base, multi-stage build), `docker-compose.yml`, volume for config/DB, env-var overrides, non-root user, healthcheck. Multi-arch (amd64 + arm64).
- **Auth**: login system with hashed passwords, session/JWT handling, logout, change password, first-run setup wizard to create the admin account. The Dev8 approach is fine as a baseline.
- **UI**: professionally polished, responsive, dark/light themes. Dashboard (instance health, recent runs, matched counts), Rules list + editor, Instances/config page, Run history & logs with filtering, Settings. Clean typography, consistent spacing, real empty/loading/error states. No unstyled bootstrap-looking scaffolding.
- **Field reference panel**: a slide-out side panel (or equivalent always-a-click-away system) available while editing a rule, listing every field a condition can use. Columns: **Source**, **Field**, **Data type**, **Description**, plus an example value. Searchable and filterable by source and type, grouped/collapsible by source, with click-to-insert into the expression editor and copy-to-clipboard. It must be generated by introspecting the snapshot DB schema plus the UDF/helper registry — not a hand-maintained list that goes stale — and include the helper functions, operators, and table relationships too. Where possible, show live sample values pulled from the user's actual connected instances.
- **Lightweight**: target low-end hardware — small idle RAM footprint, SQLite (no external DB, no Redis), efficient polling, lazy loading. Avoid heavy frameworks; keep the frontend bundle small.

## Suggested stack (change if you have a better justified choice)

C++ or C# , Cron for scheduling , SQlite for storing and quering the data, httpx for async I/O, qbittorrent-api, htmx/Alpin, and bootstrap  css


## Must also include

- Dry-run / simulate: preview exactly which torrents match and which actions would fire, without executing.
- Full run history and audit log: what ran, what matched, what changed, what failed, how long it took.
- Structured logging with configurable level.
- Config import/export as YAML or JSON, plus rule import/export so users can share rules.
- A library of 5–10 useful example rules (e.g. tag torrents whose media nobody has watched in 90 days, throttle upload on torrents in a specific category, move completed unwatched media to cold storage, remove a tag once a media item is watched).
- Input validation, connection-test buttons for every instance, and clear error messages.
- Graceful startup/shutdown, and safe behavior when a service is unreachable.
- `README.md` with setup, config reference, rule expression reference, and screenshots section.
- Tests for the SQL compiler (condition tree → expected SQL + params), the snapshot schema, cron handling, and action idempotency. Include a benchmark fixture with ~10,000 torrents and ~50 rules to prove evaluation stays well under a second.

## Deliverable

The complete repository: full source, no placeholder/TODO stubs in core paths, Dockerfile + docker-compose, README. Finish with a short summary of the architecture, the decisions you made, and anything you'd build next.
