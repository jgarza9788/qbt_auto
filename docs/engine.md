# The rule engine

How qbit-flow decides what to do, and why it is shaped this way.

## The model

There is **one global, ordered list of rules**. A rule is one `if → then` pair:

```
if  <watch_popularity> < 0.3 && "<Category>" == "Movies"     ← condition
then tag.add { "tag": "cold" }                                ← one action
```

That is the whole model. There is no pipeline, no per-rule schedule, no grouping
container. Everything else is either a property of a rule or a global setting.

| Concern | Where it lives |
|---|---|
| Order, enabled, stop-at-this-rule | on the `Rule` |
| Which qBittorrent instances a rule touches | `Rule.TargetFilterJson` (empty = all enabled) |
| Don't re-fire against the same torrent too often | `Rule.CooldownSeconds` |
| How often rules run, dry-run, parallelism, stop-at-first-match, paused | `AppSetting` (Settings page) |

### Why no pipelines

The earlier design had `Pipeline → Rules`, each pipeline with its own cron and its
own source list. It cost the user a container to create and name before they could
write a single rule, and three pipelines on the same 5-minute cron pulled the same
torrent list from the same qBittorrent instance three times.

The deciding observation: **there is only one source in the hot path.** Rule
evaluation reads qBittorrent torrent lists (cheap, changes on a minutes timescale)
and a *cache* of Plex/Jellyfin watch data. The expensive sources are refreshed by a
separate 6-hour job and are never touched during a pass. So a user-chosen cadence
was a knob with one sensible value — better to make it invisible and correct.

## A pass

`RuleEngineService` (a `BackgroundService`) wakes on an interval and runs one pass
through `RuleEngineRunner`:

```
tick
 ├─ engine paused?  ──────────────────────────► stop, no run recorded
 ├─ load enabled rules, ordered
 │   └─ none?  ───────────────────────────────► stop, no run recorded
 ├─ resolve the qBittorrent instances they need
 │   (any rule without a target filter pulls in all enabled instances)
 ├─ for each instance: TorrentSnapshotCache.GetAsync(id, interval)
 │     └─ refetches only if the cached snapshot is older than one interval,
 │        so N rules over one instance still cost one WebUI call
 ├─ for each torrent (in parallel, "CPU speed" wide):
 │     for each rule, in order:
 │       evaluate the compiled expression against
 │         drive fields ← torrent fields ← media/derived fields
 │       matched && !dryRun && in cooldown?  → skip, count, maybe stop
 │       otherwise invoke the action handler
 │       matched && stop-at-this-rule?       → next torrent
 └─ write one RunHistory + per-rule counts + the log, prune to 200 runs
```

Notes that are easy to get wrong:

- **Handlers run even when the condition is false.** `tag.sync` relies on it to
  *remove* a tag it previously added. The handler's `ActionOutcome`, not the match,
  decides what counts as applied.
- **Dry-run neither consumes nor is blocked by a cooldown** — it reports what
  *would* happen, so it must not mutate throttle state.
- **A pass with no enabled rules writes no run record**, so an idle install doesn't
  accumulate empty rows.
- **A pass is tied to host shutdown, never to the HTTP request** that triggered it.
- **Passes are single-flighted.** A slow pass causes the next tick to be skipped,
  not queued.

## Cadence and freshness are the same number

`AppSetting.QbtFreshnessSeconds` (Settings → "Rule check interval", default 120 s,
floor 30 s) is both:

- how often a pass runs, and
- the maximum age of a shared torrent snapshot.

Tying them together is what guarantees a pass never evaluates data older than one
cycle, and it leaves the user with one number instead of two that can disagree.

## Caches

| Cache | Lifetime | Invalidated by |
|---|---|---|
| `TorrentSnapshotCache` | singleton, per qBt instance | `SourceCacheInvalidator` on source edit/delete; age |
| `SourceAdapterFactory` | singleton, per source | same |
| `RuleCooldownTracker` | singleton, in-memory only | `RuleWriter` on rule edit/delete; expiry sweep |
| `MediaScoreCache` (DB) | rows, per torrent | rewritten by each analytics refresh |

Editing a source must clear **both** the adapter and the snapshot — clearing only
the adapter leaves rules evaluating torrents fetched from the old connection for up
to one interval. `SourceCacheInvalidator` exists so callers cannot forget one.

Cooldown state is deliberately process-local: losing it on restart costs at most one
extra fire, which is not worth a table and a migration.

## Watch popularity

The analytics job (`AnalyticsService`, every 6 h) is the only thing that talks to
Plex and Jellyfin. It aggregates a recency-weighted watch total per media item,
matches every torrent to an item, buckets by qBittorrent category, and
quantile-normalises each bucket to `0..1` in `MediaScoreCache.WatchPopularity`.

Rules read it as `<watch_popularity>`. `<hotcold>` and `<plex_nview>` are kept as
live aliases of the same value so pre-rename rules and legacy `config.json` imports
keep working — see `CachedMediaEnricher`.

Normalising *within a category* is the point: "unpopular for a movie" and
"unpopular for a Linux ISO" are different bars.

## Where the code lives

| Path | Role |
|---|---|
| `QbitFlow.Engine/RuleEngine/RuleEngineService.cs` | the loop, pause, manual trigger, cancel |
| `QbitFlow.Engine/RuleEngine/RuleEngineRunner.cs` | one pass: plan → snapshot → evaluate → record |
| `QbitFlow.Engine/RuleEngine/RuleCooldownTracker.cs` | per-rule/per-torrent throttle |
| `QbitFlow.Engine/Sources/TorrentSnapshotCache.cs` | shared torrent lists |
| `QbitFlow.Engine/Sources/SourceCacheInvalidator.cs` | clears both source caches together |
| `QbitFlow.Core/Domain/AppSetting.cs` | keys + `EngineDefaults` + `EngineSettings` |
| `QbitFlow.Web/Pages/Rules.cshtml` | the rule list + editor (draft model, one Save) |
| `QbitFlow.Web/Api/RuleWriter.cs` | reconciles the posted draft list into the DB |
| `QbitFlow.Web/Api/EngineApi.cs` | `/api/engine/{run,enable,disable,cancel}` |

## Editing rules

`/Rules` holds the whole list plus the editor. Everything is edited client-side
against a draft array — adding, reordering, toggling and deleting make **no network
calls** — and the list is posted as one JSON blob in a hidden field.
`RuleWriter.ReconcileAsync` then makes the stored list match the payload exactly
(update by id, insert the rest, delete anything absent) inside a single transaction,
so a save is all-or-nothing.

Two endpoints still round-trip while editing because they need the server:
`POST /api/rules/compile` (live expression preview) and `POST /api/rules/test`
("Test against current torrents", which evaluates against real torrents and mutates
nothing).
