# qbit-flow redesign — progress tracker

> ⚠️ **HISTORICAL RECORD — do not read as current design.** This tracked phases 0–6 of the
> 2026-09-02/03 redesign. On 2026-09-04 the pipeline concept was removed entirely, so the names
> below (`Pipeline`, `PipelineRunner`, `PipelineEdit`, `SchedulerService`, `hotcold`,
> `HotColdScore`) no longer exist. For how the engine works now see
> [`docs/engine.md`](docs/engine.md); `hotcold` is now `watch_popularity`.
>
> Working doc so the redesign can be paused and resumed across sessions.
> Full plan: `C:\Users\JGarza\.claude\plans\let-s-redesign-this-i-mutable-planet.md`
> Branch: `Dev8` · started 2026-09-02

## Legend
- [ ] not started · [~] in progress · [x] done · [!] blocked / needs decision

---

## Phase 0 — Solution skeleton   ✅ COMPLETE (2026-09-02)
6-project `QbitFlow.sln`, `QbitFlow.Web` boots with `/healthz` + `/health` + EF migrate-on-start,
Dockerfile + compose + `.dockerignore`, CI updated to build/test `QbitFlow.sln` + a `docker` job.
`dotnet-ef` is a local tool (`.config/dotnet-tools.json`).

## Phase 1 — Domain + config migration + engine   ✅ COMPLETE (2026-09-02)

- [x] 1.1  **Core domain model** — `SourceConnection`, `Pipeline`(+`PipelineSource`),
           `Rule`(+`RuleConditionGroup`/`RuleCondition`/`RuleAction`), `RunHistory`(+`RunRuleResult`/
           `RunLogEntry`/`ScriptRunMarker`), `MediaItem`/`MediaFilePath`/`MediaSourceStat`/
           `MediaScoreCache`, `AppSetting`, enums — `src/QbitFlow.Core/Domain/`
- [x] 1.2  **Expressions** — `PlaceholderReplacer` (regex `<key>` sub), `CriteriaEvaluator`
           (DynamicExpresso + `contains`/`match`/`daysAgo`, 2s timeout, `bool?` contract),
           `FieldCatalog` (42 fields), `ConditionCompiler` (builder tree → expression string)
- [x] 1.3  **`AppDbContext`** with all DbSets + fluent config; **enums stored as text**,
           **`DateTimeOffset` stored as UTC-ticks `long`** (SQLite can't ORDER BY / compare
           `DateTimeOffset`). WAL + `busy_timeout=30s` enabled in `AddQbitFlowInfrastructure`.
           Single migration `Data/Migrations/*_Initial.cs` (16 tables).
- [x] 1.4  **Action registry** — `IActionHandler` + `ActionRegistry` (Scrutor scan). Handlers:
           `tag.sync` / `tag.add` / `tag.remove` / `category.set` / `torrent.move` / `speed.limit`
           / `script.run` / `seeding.start` / `seeding.stop` / `seeding.forceStart`.
           `ProcessRunner` (was `Utils/Cmd`), `DriveDataProvider` (was `Utils/DriveInfo`),
           `Quantile` (was `Misc.NormalizeQuantile`) ported into `QbitFlow.Engine`.
- [x] 1.5  **`EvaluationContextBuilder`** — merges drive + torrent + media(enricher) fields.
           `NullMediaEnricher` for Phase 1 (`hotcold`/`watch_total` = 0/unmatched); real one is Phase 3.
- [x] 1.6  **`PipelineRunner`** — the 1‑2‑3 cycle: refresh only stale qBt target(s) (down target →
           logged + skipped, not fatal) → bounded-parallel torrent loop, sequential rules, dry-run
           = log-only → write `RunHistory`/`RunRuleResult`/`RunLogEntry`, update `NextRunUtc`.
           Uses short-lived `IDbContextFactory` contexts + a hardened finalise block.
- [x] 1.7  **`SchedulerService`** — internal 30s tick, per-pipeline `SemaphoreSlim` overlap lock,
           global cap of 2, `TriggerNowAsync` for "Run now", stale-`IsRunning` cleanup on start.
           `Schedule.Next` (Cronos, hard 5-min floor).
- [x] 1.8  **Sources** — `QbtGateway` (`IQbtAdapter` + `IQbtActionTarget`, lazy login, re-auth on
           403), `TorrentMapper`, `QbtConnectionInfo`; `QbtGatewayFactory` (Engine) resolves+caches
           per `SourceConnection`, decrypts the secret. `Base64SecretProtector` (`none` mode).
- [x] 1.9  **`ConfigImportService`** — JSON5 `config.json` → `SourceConnection`(qbt/plex) + one
           disabled/dry-run `Pipeline` of raw-mode rules (verbatim `Criteria`) + one `RuleAction`
           each. SHA-256 hash gate → idempotent. First-boot auto-import via `CONFIG_IMPORT_PATH`.
- [x] 1.10 **Web API + dashboard** — `Program.cs` wires infra + engine + hosted scheduler;
           `GET /` server-rendered overview; `/api/fields`, `/api/actions`, `/api/pipelines`
           (list/get/create/update/delete/enable/disable/run), `/api/runs` (list/get/logs),
           `/api/sources`, `POST /api/config/import`. `ProblemDetails` + `UseStatusCodePages`.
- [x] 1.11 **Tests** — 37 passing: `PlaceholderReplacer`, `CriteriaEvaluator`, `ConditionCompiler`,
           `Quantile`, `Schedule`, action handlers vs `FakeQbtActionTarget`, `ConfigImport`
           (real SQLite + idempotency), **`PipelineRunner`** end-to-end (dry-run counts only /
           live applies `addTag:h1:small` only).

### Verified live (`dotnet run`)
- `/healthz` 200 · `/` dashboard renders · `POST /api/config/import` imports 1 source + 1 rule
- `POST /api/pipelines/{id}/run?dryRun=true` → run completes `Succeeded`, unreachable qBt target
  logged + skipped (`errorCount=1`), `RunLogEntry` rows + `RunRuleResult` persisted, `NextRunUtc` set.
- Legacy `qbt_auto.sln` still builds (parity reference).

### Deviations from the plan
- Reusable `Utils/*.cs` were **re-implemented** (not `git mv`) in the new projects; the legacy
  `Utils/` + `Objects/` stay until a parity check is run, then get deleted (end of Phase 1 / Phase 2).
- `Serilog RunLogSink` (SSE plumbing) deferred to Phase 4 — Phase 1 writes `RunLogEntry` rows
  directly from the runner (readable via `GET /api/runs/{id}/logs`).
- FluentAssertions pinned to **7.2.2** (last Apache-2.0 version); Scrutor pinned to **6.1.0**
  (7.x wants net10 DI abstractions).

---

## Phase 2 — Sources abstraction + multi-instance + Jellyfin   ✅ COMPLETE (2026-09-02)

- [x] 2.1  **`IMediaSourceAdapter`** (`TestAsync` / `FetchMediaAsync` / `FetchWatchAsync`) +
           `MediaRecord` / `WatchRecord` / `MediaFile` records — `QbitFlow.Core.Abstractions`
- [x] 2.2  **`PlexAdapter`** (`Sources/Plex/`) — refactor of `Utils/Plex.cs`: `plex.tv` sign-in **or**
           a supplied token (`AuthMode.PlexToken`), XML parsing of `/library/sections`,
           `/library/sections/{k}/all`, `/library/metadata/{k}/allLeaves`,
           `/status/sessions/history/all` (filtered by `since`, grouped by title). Re-auth on 401.
- [x] 2.3  **`JellyfinAdapter`** (`Sources/Jellyfin/`) — NEW. `X-Emby-Token` auth, `/System/Info`,
           `/Items` catalog, per-user `/Users/{id}/Items` watch data; `PlayCount` summed across users
           (scope `all` or a named list), episodes grouped by series.
- [x] 2.4  **`SourceConnectionReader`** (`Infrastructure/Config/`) — loads a connection, decrypts the
           secret, overlays env vars: `SOURCE__<NAME>__BASEURL|USERNAME|SECRET` + shortcuts
           `QBT_*` / `PLEX_*` / `JELLYFIN_*` (env always wins, never persisted).
- [x] 2.5  **`SourceAdapterFactory`** (`Engine/Sources/`) — one factory for every kind; replaces
           `QbtGatewayFactory`, implements `ISourceAdapterFactory` **and** `IQbtGatewayFactory`.
           Caches gateways/adapters; named `IHttpClientFactory` clients `plex` / `jellyfin`.
- [x] 2.6  **`SourceHealthService : BackgroundService`** — polls every enabled source every 60s,
           writes `HealthState` / `LastCheckedUtc` / `LatencyMs` / `LastError`.
- [x] 2.7  **Secrets** — `DataProtectionSecretProtector` + `SecretProtectorFactory` selecting on
           `SECRETS_ENCRYPTION` (`none` = base64 default, `dpapi` = DP key ring at `SECRETS_KEY_DIR`).
- [x] 2.8  **Sources API** — full CRUD, `POST /{id}/test` (probes + persists health),
           `GET /{id}/sample` (media source preview). Pipeline create/update now takes `sources[]`
           with `roles` flags; runner already fans out to every `ActionTarget`.
- [x] 2.9  **Legacy retired** — `Objects/`, `Utils/`, `Program.cs`, `QbtAuto.cs`, `qbt_auto.csproj`,
           `qbt_auto.sln`, `Program_orginal.txt`, `NLog.config`, `buildit*.cmd` deleted.
           `.dockerignore` cleaned. Only `QbitFlow.sln` remains.
- [x] 2.10 **Tests — 60 passing** (+23): Jellyfin + Plex adapters via a stub `HttpMessageHandler`
           (type/genre/duration mapping, cross-user aggregation, `since` filtering, user-scope),
           `SourceConnectionReader` env overrides (generic + kind shortcut), **13-case golden
           parity test** on the real `exampleConfig.json` criteria strings.

### Verified live
- Created Plex + Jellyfin + qBt sources via API; `POST /{id}/test` returns a clean
  `{ok:false,error:…}` for each (adapters connect, fail, report) and persists `Unreachable` health.
- Pipeline created with `sources:[{roles:"Data,ActionTarget"},{roles:"Data"}]` — flags round-trip.
- App boots clean in Release with the legacy code gone.

## Phase 3 — Analytics / hot-cold   ✅ COMPLETE (2026-09-03)

- [x] 3.1  **`MediaKey`** (`Core/Matching/`) — filename/title normalisation: strip extension,
           brackets, non-year parens, quality tags, and everything after the last quality tag
           (release-group cruft) while keeping a trailing year / SxxExx.
- [x] 3.2  **`IMediaMatcher` + `FilenameMediaMatcher`** (`Engine/Matching/`) — ordered strategy
           chain: exact normalised filename (1.0) → path-segment (0.8) → title+year (0.7 with year,
           0.6 without); size-hint (±2 %) only disambiguates within a strategy. `MediaCatalog` is the
           in-memory index (`ByFileName` / `ByTitle` / `All`).
- [x] 3.3  **`AnalyticsService.RefreshAsync`** (`Engine/Analytics/`) — the only thing that talks to
           Plex/Jellyfin. Per media source: `FetchMediaAsync` + `FetchWatchAsync(now-2y)` (down
           source → logged + skipped). Upserts `MediaItem` (identity = type + normalised title +
           year, so the same title from Plex **and** Jellyfin merges) + `MediaFilePath` +
           `MediaSourceStat`. Aggregates `WeightedWatchTotal = Σ weights·window-count`
           (`AnalyticsWeights`, default all .01 / year .5 / month .9 / week 1.0; flat `PlayCount`
           = "all"). Buckets every torrent on every qBt instance by category (uncategorized →
           `"(uncategorized)"`), `Quantile.NormalizeQuantile` per bucket → `MediaScoreCache`
           (`HotColdScore` 0..1, `WatchTotal`, `DaysSinceLastWatched`, `IsMediaMatched`).
           `LegacyScore` = per-media-type quantile of the weighted total.
- [x] 3.4  **`AnalyticsRefreshService : BackgroundService`** — own interval
           (`AppSetting["AnalyticsIntervalMinutes"]` def 360, 5-min floor), `SemaphoreSlim(1)`
           single-flight, `TriggerAsync` for on-demand.
- [x] 3.5  **`CachedMediaEnricher : IMediaEnricher`** — replaces `NullMediaEnricher`. Reads
           `MediaScoreCache[(qbtInstanceId, hash)]` + the matched `MediaItem` → `hotcold`,
           `watch_total`, `days_since_last_watched` (99999 when never), `is_media_matched`,
           `media_*` fields, `plex_*` aliases (`plex_nview` = `hotcold`, `plex_nview_legacy` =
           `LegacyScore`).
- [x] 3.6  **Migration `Analytics`** — adds `MediaItem.WeightedWatchTotal` / `LegacyScore` /
           `LastWatchedUtc`.
- [x] 3.7  **API** — `GET /api/analytics/status`, `/scores?qbtInstanceId=&category=`, `/unmatched`,
           `POST /api/analytics/refresh`. Registered `AnalyticsRefreshService` hosted service.
- [x] 3.8  **Tests — 74 passing** (+14): `MediaKey` normalisation, `FilenameMediaMatcher` strategy
           precedence + no-match, **`AnalyticsService` end-to-end** (cross-source watch aggregation
           `(10+5)*0.01=0.15`, per-category quantile bucketing, unmatched torrent isolated in its
           own bucket, `CachedMediaEnricher` serving the score + `plex_nview` alias).

### Verified live
- `POST /api/analytics/refresh` → 202, runs cleanly with no sources (`lastRunUtc` set, 0 items);
  background `AnalyticsRefreshService` fires 15 s after boot and is single-flighted with the manual
  trigger. `/api/fields` exposes `hotcold` / `plex_nview` / `watch_total` / `is_media_matched`.
## Phase 4 — Web UI (HTMX) + rule builder + live run (SSE)   ✅ COMPLETE (2026-09-03)

- [x] 4.1  **Razor Pages** (`src/QbitFlow.Web/Pages/`, 12 `.cshtml`) — `_Layout` (nav + antiforgery
           token in `hx-headers` + `<meta csrf>`), `Index` (dashboard: pipeline cards + run-now/toggle,
           recent runs, source/analytics summary), `Pipelines` (+ create), `PipelineEdit` (schedule
           interval/cron + source role matrix), `Rules`, `Runs`, `RunDetail`, `Sources` (CRUD form +
           HTMX `Test`), `Analytics` (buckets, hottest, unmatched, `Refresh now`), `Settings`.
           Vendored `wwwroot/lib/{htmx.min.js, htmx-ext-sse.js, sortable.min.js}` + `app.css` (dark).
- [x] 4.2  **Rule builder** (`Rules.cshtml` + vanilla JS) — rule list with sortable drag →
           `POST /api/pipelines/{id}/rules/reorder`; enable toggle; editor with Builder/Raw modes,
           field/operator/value condition rows (fields from `FieldCatalog`, actions from
           `ActionRegistry`, both embedded server-side), live compiled-expression preview
           (`POST /api/rules/compile` debounced), action param JSON, "Test against current torrents".
- [x] 4.3  **`RulesApi`** — `GET/POST /api/pipelines/{id}/rules`, `PUT/DELETE /api/rules/{id}`,
           `POST /.../rules/reorder`, `POST /api/rules/compile` (via `ConditionCompiler`),
           `POST /api/pipelines/{id}/test-rule`. Builder trees persist as `RuleConditionGroup` +
           `RuleCondition`; old tree wiped on update.
- [x] 4.4  **`PipelineRunner.PreviewRuleAsync`** — evaluate-only: fetches the pipeline's target
           torrents, evaluates an expression, returns `{torrentName, category, matched}` rows — no
           DB writes, no run record.
- [x] 4.5  **SSE live log** — `IRunLogPublisher` (Core) + `RunLogBus` (Web, in-proc fan-out with a
           2000-line ring buffer). `PipelineRunner` publishes each `Emit` + `Complete` at the end.
           `GET /api/runs/{id}/stream` replays the DB log then streams `event: log` frames; the
           RunDetail page opens an `EventSource` while the run is `Running` and reloads on `done`.
- [x] 4.6  **Tests — 85 passing** (+11): `WebApplicationFactory` page-render smoke (6 pages + 4
           static assets) + `/api/rules/compile` shape. Disabled xUnit test parallelization
           (`AssemblyInfo.cs`) — multiple `WebApplicationFactory` instances raced on EF migrate.
           Background schedulers skipped when env == `Testing`.

### Verified live
- All pages render 200; static assets serve. Source form CRUD (302). Rule builder: `compile`,
  add/PUT/delete, reorder (B↔A order swap confirmed), test-rule (evaluate-only, graceful when
  target down). Run-now → RunDetail → **SSE stream replayed the finished run's log**. Production
  boot still starts the three hosted services (analytics refresh fired 15 s after boot).
## Phase 5 — New actions + auth + polish + docs   ✅ COMPLETE (2026-09-03)

- [x] 5.1  **`torrent.export`** action — `QBittorrent.Client` 1.9.x has **no** export wrapper, so
           `QbtGateway` opens its own cookie-jar `HttpClient`, does `POST /api/v2/auth/login`, then
           `GET /api/v2/torrents/export?hash=`. Handler writes `<name>.torrent` to `EXPORTS_DIR`
           (default `exports`), idempotent (skip if the file exists), re-auth on 403.
- [x] 5.2  **`AuthGate` middleware** — `AUTH_MODE` env (overrides `AppSetting["AuthMode"]`) ∈
           `none | apikey | basic`; `AUTH_SECRET` env (SHA-256) overrides `AppSetting["AuthSecretHash"]`.
           `apikey`: `X-Api-Key` header / `?apikey=` (sets a `qf_key` cookie) / cookie. `basic`:
           HTTP Basic, secret matches the password or username; 401 + `WWW-Authenticate` challenge.
           Exempt: `/healthz`, `/health`, `/app.css`, `/lib/*`, `/favicon.ico`. Fails **open** if
           mode is set but no secret. Settings page has a mode + secret form (hidden when
           `AUTH_MODE` is from env).
- [x] 5.3  **`POST /api/runs/{id}/cancel`** — `SchedulerService` keeps a per-pipeline linked
           `CancellationTokenSource` (tied to **host shutdown, never a request** — fixed a latent
           bug where a manual run was cancelled when the trigger response returned). RunDetail page
           gets a "Cancel run" button while running.
- [x] 5.4  **Run/log pruning** — `PipelineRunner` keeps the last 50 `RunHistory` per pipeline
           (+ their `RunLogEntry` / `RunRuleResult`), best-effort via `ExecuteDeleteAsync`.
- [x] 5.5  **Polish** — EF `UseQuerySplittingBehavior(SplitQuery)` (kills the multi-include warning);
           `PathDiagnosticsService` (one-shot ~20 s after boot: warns about qBt save-paths not
           visible in-container); Dockerfile `EXPORTS_DIR` / `SECRETS_KEY_DIR` env + `/data/keys`.
- [x] 5.6  **CI / docs** — `build.yml` = build + test only; new `docker.yml` = multi-arch
           (`linux/amd64,linux/arm64`) GHCR build, push on branch/tag (build-only on PR).
           `docker-compose.yml` finalised (all env documented). **README fully rewritten** for
           qbit-flow. New **`docs/migration.md`**.
- [x] 5.7  **Tests — 93 passing** (+8): `torrent.export` handler (writes file / skips existing /
           dry-run / not-applicable); `AuthGate` (`/healthz` exempt, apikey reject→accept, basic
           challenge→accept, stable hash). Live: apikey gate verified end-to-end
           (`/` 401 → `X-Api-Key` 200 → `?apikey=` 200), `torrent.export` in `/api/actions`.

---

# ✅ REDESIGN COMPLETE — all 6 phases (0–5) done.

`QbitFlow.sln` · 108 source files + 12 Razor pages · **93 tests green** · clean Release build ·
legacy console app deleted. Nothing committed yet — everything is on disk.

Suggested first commit: everything under `src/`, `tests/`, `QbitFlow.sln`, `.config/`, `Dockerfile`,
`docker-compose.yml`, `.dockerignore`, `docs/`, `.github/workflows/*`, plus the tracked deletions of
the legacy files and the `README.md` / `.gitignore` / `build.yml` edits.

---

## Resume / sanity checklist
1. `dotnet build QbitFlow.sln` + `dotnet test QbitFlow.sln` → expect green, **93 tests**.
2. Run locally: `ConnectionStrings__Db="Data Source=$TMP/qf.db" dotnet run --project src/QbitFlow.Web`
   then open the printed URL (UI at `/`). `docker compose up` for the container.
3. All phases are done. Remaining work is optional hardening / features, not part of the plan:
   nested condition groups in the builder UI, per-schema action param forms, `torrent.export` UI
   button, coverage for the SSE stream, and committing the tree.
