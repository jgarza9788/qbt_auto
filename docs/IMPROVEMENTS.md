# qbitflow — How to Improve

_Assessment written 2026-09-05, branch `Dev10`. Based on a full read of the codebase against
`fable-prompt-qbitflow.md`. No code was changed to produce this document._

## Context

`qbitflow` is the Fable one-shot rewrite of the old `qbt_auto` tool: a long-running,
Dockerized ASP.NET Core (.NET 9) Razor Pages + HTMX app, SQLite only, that evaluates
user-defined rules (cron + condition + actions) against a live snapshot of qBittorrent,
Plex, Jellyfin, Tautulli, Jellystat, Jellyglance, and disk-usage data.

**Current state:** builds clean, 132 tests pass (incl. the 10k-torrent / 50-rule benchmark),
runs in Docker (after the volume-ownership fix in `docker-entrypoint.sh`). The architecture
is sound and the core paths are real, not stubbed. What follows is the gap list, ordered by
value-per-effort.

The container volume bug that blocked first boot (`SQLite Error 14: unable to open database
file`) is already fixed on disk: `docker-entrypoint.sh` + Dockerfile changes make the
entrypoint start as root, `chown` the data dir, then drop to `app` via `gosu`.

---

## Tier 1 — Broken or no-op features (fix first)

These are implemented but not wired, so they silently do nothing. A user who trusts the UI
gets wrong behavior with no error.

### 1.1 Jellystat & Jellyglance watch timestamps are always NULL

- **Where:** `src/Qbitflow.Sources/Json/JsonPathResolver.cs:60` (`GetUnixSeconds`), used at
  `src/Qbitflow.Sources/Adapters/RestHistoryAdapterBase.cs:83`.
- **Problem:** `GetUnixSeconds` only accepts a numeric epoch. Tautulli's default field `date`
  is numeric → fine. Jellystat's default `ActivityDateInserted` and Jellyglance's `watchedAt`
  are ISO-8601 strings → parse returns null → `watch_history.watched_at` is NULL for those two
  sources.
- **Downstream breakage:** `days_since_watched`, the `ix_watch_history_watched_at` index path,
  `play_counts` first/last-watched timestamps, and every "no watch history in the last N days"
  rule — all dead for 2 of the 3 history sources. This is close to the whole point of the app.
- **Fix:** add ISO-8601 / RFC3339 parsing to a new `GetTimestamp(element, field)` helper
  (try `long` epoch seconds, then epoch millis, then `DateTimeOffset.TryParse` with
  `DateTimeStyles.AssumeUniversal | AdjustToUniversal`). Point `RestHistoryAdapterBase` at it.
- **Test:** extend `src/Qbitflow.Tests/Sources/` history-adapter tests with an ISO-timestamp
  fixture for Jellystat and Jellyglance; assert `WatchedAt` is populated.
- **Effort:** ~1 hr.

### 1.2 `torrent_files` is never populated

- **Where:** `src/Qbitflow.Engine/RuleRunner.cs:98-106` builds `SnapshotInput` from cache with
  only `Torrents`, `MediaItems`, `WatchHistory`. `SourceFetchResult`
  (`src/Qbitflow.Core/Domain/SourceData/SourceFetchResult.cs`) has no `TorrentFiles` list.
  `QbtAdapter.GetFilesAsync` / `IQbtTorrentFilesProvider` is fully implemented but has zero
  non-test callers. `SnapshotDatabase.RebuildTorrentFiles` hardcodes `instance_id = 0`.
- **Problem:** file-level torrent↔library matching does not function. `torrent_files` as a
  queryable relation isn't exposed in `SnapshotFieldRegistry` either. The common real scenario
  — a season-folder torrent vs. per-episode files in Plex/Jellyfin — cannot correlate, because
  the EXISTS join is exact `path_key` equality on `torrents.path_key` only.
- **Fix (two parts):**
  1. Add `TorrentFiles` to `SourceFetchResult`; have `QbtAdapter.FetchAsync` populate it
     (bounded — files only for torrents matching some size/count threshold, or lazily) and
     carry it through the cache into `SnapshotInput.TorrentFiles`. Fix the `instance_id`
     hardcode in `RebuildTorrentFiles`.
  2. Either expose `torrent_files` in `SnapshotFieldRegistry.Relations` + `ConditionSqlCompiler`
     so a rule can say "exists a torrent file whose path_key matches a media item", **or**
     keep it internal and add a prefix-match option to the EXISTS correlation
     (`inner.path_key LIKE outer.path_key || '/%'`) guarded so it still uses an index.
- **Decision needed:** how much of this to do. Minimum viable: populate the table + expose the
  relation. Full: prefix-aware correlation in the visual builder.
- **Effort:** 0.5–1.5 days depending on scope.

### 1.3 `Priority` and `StopOnMatch` are never enforced

- **Where:** `src/Qbitflow.Core/Domain/Rule.cs:18-22` (fields), shown in
  `src/Qbitflow.Web/Pages/Rules/Index.cshtml`, exported in `ConfigPortabilityService`.
  `src/Qbitflow.Engine/Scheduling/RuleSchedulerService.cs` fires each due rule as an
  independent fire-and-forget task; `RuleRunner.RunAsync(int ruleId)` runs one rule with its
  own fresh `SnapshotDatabase` (`RuleRunner.cs:91`).
- **Problem:** no cross-rule "cycle." `StopOnMatch` is a no-op. `Priority` only affects list
  display order, not execution. The spec's "all rules in a cycle evaluate against the same
  immutable snapshot" is not honored — each rule rebuilds its own.
- **This is a design fork — pick one:**
  - **(a) Cycle orchestrator (matches spec).** Add a scheduler tick that collects all due
    rules, builds ONE shared immutable `SnapshotDatabase`, evaluates rules in `Priority`
    order, and maintains a per-tick "already matched" torrent set that `StopOnMatch` rules
    remove from the candidate pool for lower-priority rules. Biggest single change; touches
    `RuleSchedulerService`, `RuleRunner` (split "run" from "build snapshot"), and needs a new
    `RuleCycleRunner`.
  - **(b) Lightweight.** Keep rules independent, but within one tick: sort due rules by
    `Priority`, share one snapshot, and honor `StopOnMatch` as a same-tick exclusion set.
    No full orchestrator rewrite; ~60% of the value.
  - **(c) Cut it.** Remove `Priority` / `StopOnMatch` from model + UI + export. The README
    already defends "each rule runs independently, different rules have different schedules."
    Honest and simple; loses a spec capability.
- **Effort:** (a) 2–3 days · (b) ~1 day · (c) ~2 hrs.

### 1.4 Recursive folder size never computes at runtime

- **Where:** `src/Qbitflow.Sources/Storage/StorageUsageService.cs` —
  `GetOrComputeFolderSizeAsync` has no caller outside
  `src/Qbitflow.Tests/Sources/StorageUsageServiceTests.cs`. `RuleRunner.cs:95` calls only
  `GetUsage`, which merely *reads* the folder-size cache that nothing populates.
- **Problem:** `folder_size_bytes` / `folder_size_gb` / `folder_size_computed_at` are always
  NULL. Any rule using `storage.<name>.folder_size_gb` silently never matches.
- **Fix:** add a lightweight `BackgroundService` (`StorageFolderSizeScanner`) that iterates
  enabled `StoragePaths`, calls `GetOrComputeFolderSizeAsync` (which already honors the
  per-path `FolderSizeScanIntervalMinutes` TTL and does an iterative walk), and runs on a
  loose timer (e.g. every 5 min it checks which paths are due). Skip in `ASPNETCORE_ENVIRONMENT
  == "Testing"` like the other hosted services. Alternatively call it inline in `RuleRunner`
  before `GetUsage`, but a background scanner keeps the expensive walk off the rule path.
- **Effort:** ~2 hrs.

### 1.5 "Run now" bypasses the overlap gate

- **Where:** `src/Qbitflow.Web/Pages/Rules/Index.cshtml.cs:44` calls
  `ruleRunner.RunAsync(id, ct)` directly; `RuleRunGate` is only consulted by
  `RuleSchedulerService`.
- **Problem:** a manual run can overlap an in-flight scheduled run of the same rule → two
  concurrent snapshots and action passes for one rule.
- **Fix:** wrap the manual-run handler in `RuleRunGate.TryEnter(ruleId)` / `Exit`; if already
  in flight, return a "rule is already running" message to the page. Consider moving the
  gate acquisition into `RuleRunner.RunAsync` itself so every caller is covered.
- **Effort:** ~30 min.

### 1.6 Theme setting is a dead control

- **Where:** `src/Qbitflow.Web/Pages/Settings/Index.cshtml.cs` persists `AppSettings.Theme`;
  `Shared/_Layout.cshtml` only ever reads `localStorage['qbitflow-theme']` via the navbar
  toggle. The two are unlinked.
- **Fix:** on server render, emit the persisted `AppSettings.Theme` as the initial
  `data-bs-theme` (and as a `<meta>` or inline bootstrap value the pre-paint script reads),
  with `localStorage` still allowed to override per-browser. Or drop the Settings control and
  keep it purely client-side. Also: `Login.cshtml` / `Setup.cshtml` use `Layout = null` and
  load only `bootstrap.min.css` — they look like raw Bootstrap; give them the shell + theme.
- **Effort:** ~1–2 hrs.

### 1.7 Example rules aren't shipped and can't be loaded from the UI

- **Where:** `Dockerfile` does `COPY src/ src/` only — `examples/example-rules.json` never
  lands in the image. No "load examples" button; README §"Example rules" says to import from
  Settings.
- **Fix:** `COPY examples/ ./examples/` in the runtime stage (or embed the JSON as an assembly
  resource in `Qbitflow.Web`), and add a "Load bundled examples" button on the Rules page or
  the Settings import block that reads the shipped file and runs it through the existing
  `ConfigPortabilityService` rules-import path.
- **Effort:** ~1–2 hrs.

### 1.8 `docker-compose.yml` only works on the author's machine

- **Where:** `docker-compose.yml`.
- **Problems:** hard-coded `build.context: /home/tbp/projects/qbitflow/`; undocumented
  `${TIMEZONE}` / `${QBITFLOW_LOCATION}` with no `.env.example`; README claims a named volume
  `qbitflow-data` but compose bind-mounts `${QBITFLOW_LOCATION}/data`; README says open `:8080`
  but compose maps `8087:8080`; compose healthcheck hits `localhost` while the Dockerfile uses
  `127.0.0.1`.
- **Fix:** `build.context: .`; ship a named volume `qbitflow-data` as the default with the
  bind-mount shown commented-out; add `.env.example` with `TZ` and any real knobs; align the
  port and the README; make the healthcheck consistent. Decide on ONE canonical port and use
  it everywhere.
- **Effort:** ~1 hr.

---

## Tier 2 — Real spec gaps (asked for, not present)

### 2.1 No CI

`.github/workflows/` is an empty directory. Spec wanted a workflow that builds, tests, and
publishes the image. Add `.github/workflows/ci.yml`: `dotnet build` + `dotnet test` on push/PR,
and a `docker/build-push-action` job (see 2.2) on tags. **Effort:** ~2–3 hrs.

### 2.2 No multi-arch Docker image

No `buildx`, no `--platform`, no `TARGETARCH`. Spec wanted amd64 + arm64. The Dockerfile is
already arch-agnostic (framework-dependent publish); just needs a `buildx` job with
`platforms: linux/amd64,linux/arm64` and QEMU setup in CI. **Effort:** ~2 hrs (mostly CI).

### 2.3 No plain-text expression editor with autocomplete

Spec wanted **both** the visual builder and a text expression editor for power users. Only the
visual builder + a raw-SQL `<textarea>` (no completion) exist. Options: (a) a small expression
grammar (`category == "x" and days_since_added > 30`) that compiles to the same
`ConditionNode` tree, with a CodeMirror/Monaco-lite autocomplete fed by `SnapshotFieldRegistry`;
(b) downgrade scope — add field/function autocomplete to the existing Advanced-SQL textarea and
call that the power-user path. **Effort:** (a) 2–4 days · (b) ~1 day.

### 2.4 Field reference panel is half-finished

- **Where:** `src/Qbitflow.Web/Pages/Rules/Edit.cshtml` (`#fieldRefPanel` offcanvas),
  `wwwroot/js/rule-editor.js` (`fieldReferencePanel`),
  `src/Qbitflow.Engine/Conditions/SnapshotFieldRegistry.cs`,
  `Rules/Edit.cshtml.cs` `BuildFieldRegistryPayload`.
- **Missing vs spec:** click-to-insert into the editor (currently copy-only; `insertField`
  exists in the builder JS but nothing calls it from the panel); grouped/collapsible by source
  (currently a flat filtered list); the **example-value column is in the payload but never
  rendered**; no live sample values from connected instances; operators live only in
  `OPERATORS_BY_TYPE` in JS (second source of truth); table relationships not surfaced.
- **Fix, in priority order:** render the example column; wire click-to-insert; add an accordion
  by source; (stretch) a `?handler=SampleValues` endpoint that pulls a few live values from the
  most recent snapshot/cache per field.
- **Effort:** ~1 day for the first three; +1 day for live samples.

### 2.5 Parallelism doesn't apply to action execution

- **Where:** `src/Qbitflow.Engine/Actions/ActionExecutor.cs:27,57` — sequential
  `foreach (group) { foreach (action) }`. Constructor takes no `IHostConcurrencyLimiter` /
  `IParallelismSettingsProvider`. The fetch path is throttled per-host
  (`HostConcurrencyLimiter`), the action path isn't at all.
- **Fix:** inject `IHostConcurrencyLimiter` + `IParallelismSettingsProvider`; run instance
  groups through a bounded `Parallel.ForEachAsync` / `SemaphoreSlim` sized off the parallelism
  level, and acquire the per-host limiter around each qBt call. Also reuse the qBt SID cookie
  across a run instead of re-logging in on every `PostFormAsync` (`QbtAdapter.cs:159`).
- **Effort:** ~0.5 day.

### 2.6 "Refresh now" / cache invalidation has no UI

`ISourceDataCache.Invalidate` / `InvalidateAll` and `RefreshAsync(force: true)` exist with
zero non-test callers. Add a "Refresh data now" button on the Dashboard or Instances page →
an endpoint that calls `SourceRefreshCoordinator.RefreshAsync(connections, forceRefresh: true)`.
Also add per-instance invalidation when an instance is edited/deleted. **Effort:** ~2–3 hrs.

### 2.7 Per-rule dataset declarations

`RuleRunner` refreshes every enabled instance on every run; the TTL cache is the only thing
preventing N×API calls. Spec: "a rule declares which datasets it needs; the scheduler resolves
the union of needs, refreshes only what is stale." Approach: walk the `ConditionNode` tree (and
`ExistsNode.Relation`s) to collect referenced relations → map relations to `SourceType`s →
refresh only those instances. **Effort:** ~1 day. Lower priority — the cache makes this an
efficiency win, not a correctness one.

### 2.8 Advanced SQL `FullQuery` mode is dead code

`AdvancedSqlMode.FullQuery` is fully handled in `AdvancedSqlExecutor.Validate` but every caller
hardcodes `WhereClause` (`RuleRunner.cs:115`, `Edit.cshtml.cs:83,193`). Either add a mode
toggle to the rule editor + a `Rule.AdvancedSqlMode` field and wire it through, or delete the
`FullQuery` branch. **Effort:** ~2–3 hrs to wire · ~30 min to delete.

### 2.9 Dashboard "instance health" is static

It's an enabled/disabled badge, not a probe. Add a periodic `SourceHealthService`
(`BackgroundService`, skip under Testing) that calls each adapter's `TestConnectionAsync` on a
slow cadence and stores last-result + latency; render that on the Dashboard and the Instances
list. **Effort:** ~0.5 day.

### 2.10 Jellyfin native watch data isn't fetched

`JellyfinAdapter` fetches media items only (`/Items`), never playback data
(`/Users/{id}/Items` with `IsPlayed`, or `/System/ActivityLog`). Combined with 1.1 this means
Jellyfin users have no working watch history at all until Jellystat/Jellyglance are fixed and
configured. Add a playback fetch to `JellyfinAdapter` producing `WatchHistoryRecord`s.
**Effort:** ~0.5 day.

### 2.11 README screenshots

`## Screenshots` is a TODO stub. Once the UI is exercised in a real browser, capture Dashboard,
Rules editor (visual builder + field panel), and Run history; drop them in `docs/img/`.
**Effort:** ~1 hr once the app is running against real data.

---

## Tier 3 — Defensible deviations (recommend leaving as-is)

- **Advanced SQL uses `PRAGMA query_only` + a keyword denylist regex**, not a SQLite
  `authorizer` callback. Documented threat model is "admin mistake, not hostile author." The
  regex can false-positive on keywords inside string literals — a minor annoyance, not a hole.
  If hardening later: add an `sqlite3_set_authorizer` callback that whitelists `SQLITE_SELECT`,
  `SQLITE_READ`, `SQLITE_FUNCTION` and denies the rest.
- **`days_since()` / `size_gb()` stay as per-row UDFs** in `WHERE` rather than precomputed
  columns. The spec preferred precomputed; the 10k-row benchmark passes anyway. Revisit only
  if real datasets are much larger.
- **NL→cron only via a fixed 6-item preset list.** Arbitrary "every N minutes / daily at HH:MM"
  parsing was explicitly declined. Fine.
- **Snapshot per rule-run rather than per-cycle** — only becomes a problem if you take Tier 1.3
  option (a) or (b).
- **No SSE / live log** — the Run History page with expandable per-torrent/per-action detail is
  a solid audit surface. A live log is nice-to-have, not required.
- **Structured logging minimum level requires a restart** (read from SQLite before DI is built).
  Acceptable and documented; an env-var override (`QBITFLOW_LOG_LEVEL`) would be a cheap
  addition if desired.

---

## Suggested sequence

1. **Packaging & first-run** (1.7, 1.8) — so anyone can actually deploy it. ~half day.
2. **Watch-history correctness** (1.1, then 2.10) — restores the core value prop. ~1 day.
3. **Quick no-op fixes** (1.5, 1.6, 1.4) — cheap, visible. ~half day.
4. **Decide the rule-cycle fork** (1.3) — pick (a)/(b)/(c) and do it. 2 hrs–3 days.
5. **CI + multi-arch** (2.1, 2.2) — locks in quality before more feature work. ~1 day.
6. **torrent_files + file-level matching** (1.2) — ~1 day.
7. **Field panel finish + action parallelism + refresh-now** (2.4, 2.5, 2.6) — ~2 days.
8. **Remaining spec gaps** (2.3, 2.7, 2.8, 2.9, 2.11) as appetite allows.

## Verification for each change

- `dotnet build Qbitflow.sln` clean, `dotnet test src/Qbitflow.Tests/Qbitflow.Tests.csproj`
  green (132 baseline; add tests per item above).
- For watch-history and folder-size fixes: build the Docker image, point it at a real
  Jellystat/Jellyglance and a real path, create a dry-run rule using `days_since_watched` /
  `folder_size_gb`, run it, and confirm matches in Run History detail.
- For the cycle fork: a test with two rules, high-priority `StopOnMatch`, overlapping
  candidate torrents — assert the low-priority rule skips the already-matched ones.
- For Docker/compose: `docker compose up -d` on a clean machine with only `.env` filled in,
  reach `/Setup`, complete the wizard, import the bundled examples.
