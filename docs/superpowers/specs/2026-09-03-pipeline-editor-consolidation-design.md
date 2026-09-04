# Pipeline editor consolidation — design

**Date:** 2026-09-03
**Status:** Approved (scope: full redesign, all 4 parts)

## Problem

Editing a pipeline is split across two pages with two mental models:

- **`/PipelineEdit?id=`** — a server-rendered `<form>` (name, schedule, sources,
  parallelism, toggles), one POST.
- **`/Rules?pipelineId=`** — a client-side app that persists each rule
  individually via `/api/rules/*` and `location.reload()`s after every change.

Related rough edges:

- The schedule UI exposes an Interval/Cron choice with no field gating; picking
  Interval and leaving seconds blank silently saves `IntervalSeconds = null`.
- `Max parallelism` is a raw 1–64 number with no guidance.
- `TimeZoneId` is a free-text per-pipeline field, redundant with the container's
  `TZ`.
- The `<placeholder>` fields usable in rule expressions are only discoverable
  through the builder's dropdown; there is no reference list.

## Goal

One page — `/Pipeline?id=` — that edits everything about a pipeline as a single
form with one **Save**, organised into labelled sections, with a cron-only
schedule (presets + custom), a friendly "CPU speed" control, and a collapsible
field reference.

## Non-goals

- No change to the rule **evaluation** engine or the `Rule` / `RuleAction` /
  `RuleConditionGroup` schema.
- No change to the JSON API surface in `PipelinesApi` (`/api/pipelines/*`).
- No new cron library — `Cronos` is already referenced.
- No database migration in this change (see "Data model").

---

## Part 1 — Schedule: cron-only with presets

### UI (§ Details)

Replace the `Schedule` select + `Interval (seconds)` input + `Time zone` input
with:

1. **Preset `<select id="cron-preset">`** — options whose `value` is a 6-field
   (`CronFormat.IncludeSeconds`) expression, plus a final `Custom…` option with
   an empty value:

   | Label | Expression |
   |---|---|
   | Every 5 minutes | `0 */5 * * * *` |
   | Every 15 minutes | `0 */15 * * * *` |
   | Every 30 minutes | `0 */30 * * * *` |
   | Hourly | `0 0 * * * *` |
   | Every 6 hours | `0 0 */6 * * *` |
   | Every 12 hours | `0 0 */12 * * *` |
   | Daily at midnight | `0 0 0 * * *` |
   | Daily at 03:00 | `0 0 3 * * *` |
   | Weekly (Sun 03:00) | `0 0 3 * * 0` |
   | Custom… | *(empty)* |

2. **Text input bound to `CronExpression`** (`<input asp-for="CronExpression">`).
   This is the only bound schedule field.

**JS behaviour:** on preset change with a non-empty value → set the text box to
that value and `readOnly = true`; on `Custom…` → `readOnly = false`, focus. On
load → if `CronExpression` equals a preset value select it, else select
`Custom…`. The preset select is a helper only — not posted, not bound.

Time zone is removed from the form entirely.

### Server

`Pipeline.cshtml.cs` `OnPostAsync`:

- `p.ScheduleKind = ScheduleKind.Cron` always.
- `CronExpression` trimmed; validated with
  `CronExpression.Parse(expr, CronFormat.IncludeSeconds)` inside try/catch.
  Empty or unparseable → `ModelState.AddModelError(nameof(CronExpression), …)`
  and re-render (no save). The 5-minute floor in `Schedule.Next` is unchanged and
  still clamps sub-floor expressions.

`OnGetAsync`:

- If the loaded pipeline is `ScheduleKind.Interval`, pre-fill `CronExpression`
  from a seconds→cron map so the form opens on a valid value:
  `300→"0 */5 * * * *"`, `900→"0 */15 * * * *"`, `1800→"0 */30 * * * *"`,
  `3600→"0 0 * * * *"`, `21600→"0 0 */6 * * *"`, `43200→"0 0 */12 * * *"`,
  `86400→"0 0 0 * * *"`; anything else → `"0 */15 * * * *"`.

### Time zone from the container

`src/QbitFlow.Engine/Scheduling/Schedule.cs`:

```csharp
public static DateTimeOffset Next(Pipeline pipeline, DateTimeOffset fromUtc, TimeZoneInfo? tz = null)
```

- `tz ??= TimeZoneInfo.Local`. On Linux, `TimeZoneInfo.Local` is derived from the
  `TZ` env var (the compose `TZ=${TIMEZONE}` value); unset → container default
  (UTC).
- Stop reading `pipeline.TimeZoneId`. Remove the private `ResolveTimeZone`
  helper.
- `SchedulerService` calls `Schedule.Next(pipeline, now)` unchanged (picks up
  `TimeZoneInfo.Local`). `ScheduleTests` passes an explicit `tz` so the cron
  cases stay deterministic on CI.

`Pipeline.TimeZoneId` stays as a column (no migration) but is no longer consulted
by the engine. `PipelinesApi` continues to accept the field; it becomes a
documented no-op.

---

## Part 2 — "CPU speed"

Replace the `MaxParallelism` number input with a `<select>`:

| Label | `MaxParallelism` |
|---|---|
| Low | 2 |
| Medium *(default)* | 8 |
| High | 16 |
| SuperHigh | 32 |

- `Pipeline.cshtml.cs`: `[BindProperty] public string CpuSpeed { get; set; } = "Medium";`
- New static helper `QbitFlow.Web.Pages.CpuSpeedMap` (distinct name from the
  bound property) with:
  - `IReadOnlyList<(string Label, int Value)> Tiers` = the table above.
  - `int ToValue(string label)` — exact label match, else `8`.
  - `string ToLabel(int value)` — nearest tier by absolute distance
    (`7→Medium`, `20→High`, `100→SuperHigh`, `1→Low`).
- `OnGetAsync`: `CpuSpeed = CpuSpeedMap.ToLabel(p.MaxParallelism);`
- `OnPostAsync`: `p.MaxParallelism = Math.Clamp(CpuSpeedMap.ToValue(CpuSpeed), 1, 64);`
- DB column, `Pipeline.MaxParallelism`, and `PipelinesApi` (raw int) unchanged —
  a value set via the API that is not exactly 2/8/16/32 simply displays as the
  nearest tier.

---

## Part 3 — One page, one form, one Save

### The page

`src/QbitFlow.Web/Pages/Pipeline.cshtml` + `.cshtml.cs`, route `?id=` (with
`?pipelineId=` accepted as an alias in `OnGetAsync`/`OnPostAsync` so existing
links survive). Replaces both `PipelineEdit.*` and `Rules.*`.

Layout, one `<form method="post">` wrapping the lot:

- `<h1>` + a thin in-page anchor nav: **Details · Sources · Rules · Fields**.
- **§ Details** — name, schedule (Part 1), CPU speed (Part 2), the
  Enabled / Dry-run / Stop-on-first-match checkboxes.
- **§ Sources** — the existing Data / Action-target table (`DataSourceIds`,
  `TargetIds` checkboxes), unchanged markup.
- **§ Rules & actions** — the current `Rules.cshtml` shell (rule table with
  drag-reorder, the editor panel, "test against current torrents"), moved over.
  Its JS becomes a **draft model**: add / edit / reorder / remove mutate an
  in-memory `rules` array and re-render — **no network calls**. A hidden
  `<input type="hidden" asp-for="RulesPayload">` holds `JSON.stringify(rules)`,
  resynced on every mutation and immediately before submit.
- **§ Fields** — Part 4.
- One **Save** button (submits the form). A `beforeunload` handler warns when the
  form is dirty.

### The page model

`PipelineModel(AppDbContext db, ActionRegistry actions)` — union of today's two
models:

- Bound: `Name`, `Enabled`, `DryRun`, `StopOnFirstMatch`, `CronExpression`,
  `CpuSpeed`, `DataSourceIds`, `TargetIds`, `RulesPayload` (string).
- For render: `Pipeline`, `AllSources`, `FieldsJson`, `ActionsJson`,
  `RulesJson`, `FieldCatalog.All` (Part 4), the preset list.

### `OnPostAsync`

1. Load the pipeline (`Include(x => x.Sources)`); 404 if missing.
2. Bind + validate § Details:
   - name non-empty; `CronExpression` parses (Part 1). On any `ModelState`
     error → re-render `Page()` (the posted `RulesPayload` round-trips, so the
     rules editor rehydrates from it).
3. Apply scalar fields + `ScheduleKind = Cron` + `MaxParallelism` from `CpuSpeed`.
4. Reconcile **sources** exactly as `PipelineEdit.OnPostAsync` does today
   (remove `PipelineSources`, re-add from `DataSourceIds ∪ TargetIds`).
5. Reconcile **rules** from `RulesPayload` via `RuleWriter` (below).
6. `if (Enabled && p.NextRunUtc is null) p.NextRunUtc = DateTimeOffset.UtcNow;`
   (kept from today).
7. `p.UpdatedUtc = now; await db.SaveChangesAsync();` — single EF transaction.
8. `TempData["Msg"] = "Pipeline saved."; return RedirectToPage(new { id });`

### `RuleWriter`

`src/QbitFlow.Web/Api/RuleWriter.cs` — the rule-mutation logic lifted verbatim
from `RulesApi` (`ApplyRule`, `ToGroupEntity`, `DeleteGroupTreeAsync`), now a
reusable `sealed class RuleWriter(AppDbContext db, ConditionCompiler compiler)`:

```csharp
Task ReconcileAsync(Guid pipelineId, IReadOnlyList<RuleDraft> drafts, CancellationToken ct);
```

`RuleDraft` = the existing `RulesApi.RuleDto` shape **plus** a nullable
`Guid? Id`. Reconcile:

- Load `db.Rules.Include(Action).Include(RootGroup)…` for the pipeline.
- For each draft in payload order (index → `Order`):
  - `Id` matches an existing rule → wipe its old `RootGroup` tree, `ApplyRule`.
  - `Id` null / unmatched → `new Rule { PipelineId = pipelineId }`, `ApplyRule`,
    `db.Rules.Add`.
- Existing rules whose `Id` is absent from the payload → delete (+ their group
  trees).
- No `SaveChangesAsync` inside `RuleWriter` — the page handler owns the
  transaction.

Registered in DI as scoped. `ConditionCompiler` is already registered.

### API changes

- **Keep:** `POST /api/rules/compile`, `POST /api/pipelines/{id}/test-rule`
  (live compile preview + rule test still need a round-trip).
- **Delete:** `POST /api/pipelines/{id}/rules`, `PUT /api/rules/{id}`,
  `DELETE /api/rules/{id}`, `POST /api/pipelines/{id}/rules/reorder`. Their
  `RulesApi` handler bodies go; the shared helpers now live in `RuleWriter`.
- `GET /api/pipelines/{id}/rules` — keep (harmless, read-only; used by nothing
  after this but cheap to leave). *Decision: leave it.*
- `PipelinesApi` untouched.

---

## Part 4 — Field reference (§ Fields)

A `<details>` panel (collapsed by default), server-rendered from
`FieldCatalog.All`:

- Grouped by `FieldSource`: **Torrent**, **Media**, **Derived**. Per group a
  table: **Field** (`f.Placeholder`, e.g. `<hotcold>`, monospace) · **Type**
  (`f.Type`) · **Description** (`f.Description`) · **Example** (`f.Sample`).
- A short static blurb for **Drive** fields (not in the catalog — generated
  per-mount by `DriveDataProvider`): keys look like `<mount>_TotalSizeGB`,
  `<mount>_FreeSizeGB`, `<mount>_UsedSizeGB`; exact names depend on the
  container's mounts.
- Each field row has a small "copy" affordance that writes the `<placeholder>`
  to the clipboard (`navigator.clipboard.writeText`, vanilla, ~5 lines).

No new data plumbing — the page model exposes `FieldCatalog.All` directly.

---

## Data model

No migration in this change. Columns `ScheduleKind`, `IntervalSeconds`,
`TimeZoneId` remain but are no longer written (`ScheduleKind` is always set to
`Cron`) or read by the engine (`TimeZoneId`). A later cleanup migration can drop
all three plus `Pipeline.EffectiveIntervalSeconds`; that is explicitly out of
scope here to keep the change reversible and off the DB.

`Pipelines.cshtml.cs` `OnPostAsync` (the "create pipeline" quick form) — change
the seed from `IntervalSeconds = 900` to
`ScheduleKind = ScheduleKind.Cron, CronExpression = "0 */15 * * * *"` so new
pipelines open cleanly in the merged editor.

The dashboard/list schedule strings in `Index.cshtml.cs` and `Pipelines.cshtml.cs`
already handle the `Cron` branch (`p.CronExpression ?? "cron"`) — no change
needed; the Interval branch just stops being hit for pipelines saved from the new
editor.

---

## Error handling

- **Bad cron** → `ModelState` error under the field, form re-renders, rules draft
  preserved via `RulesPayload`. No partial save.
- **Empty name** → same.
- **Invalid rule expression** → does **not** block Save (unchanged semantics:
  `ApplyRule` stores `CompileValid = false` + `CompileError`; the runner still
  evaluates `CompiledExpression`). The rule row renders with its existing error
  badge.
- **Malformed `RulesPayload` JSON** (should be impossible from the UI) →
  `ModelState` error, re-render, no save. Deserialize with a guard.
- **`beforeunload`** dirty guard so a large half-filled form is not lost to an
  accidental navigation.
- `RuleWriter.ReconcileAsync` throwing → propagates out of `OnPostAsync`;
  `SaveChangesAsync` not reached, nothing persisted.

---

## Testing

**`tests/QbitFlow.Tests/Scheduling/ScheduleTests.cs`**

- Update existing cases to pass an explicit `tz` (UTC) to `Schedule.Next`.
- Add: a preset expression (`0 0 * * * *`) → expected next occurrence.
- Add: `Schedule.Next` with `tz` omitted still returns a value (smoke — uses
  `TimeZoneInfo.Local`).

**New `tests/QbitFlow.Tests/Web/PipelinePageTests.cs`** (page-handler level,
`WebApplicationFactory<Program>` with the `Testing` environment / in-memory or
SQLite as existing web tests do)

- Save with a valid cron + two new rules → both persisted, `Order` 0/1,
  `ScheduleKind == Cron`.
- Save omitting an existing rule's `Id` from the payload → that rule (and its
  condition group) deleted.
- Save reordering the payload → `Order` follows payload index.
- Save with an unparseable cron → `ModelState` invalid, pipeline unchanged.
- `CpuSpeed` round-trips: `ToLabel(7) == "Medium"`, `ToLabel(20) == "High"`,
  `ToLabel(100) == "SuperHigh"`, `ToValue("SuperHigh") == 32`,
  `ToValue("bogus") == 8` (pure unit test, no host).
- Interval-scheduled pipeline loaded via `OnGetAsync` → `CronExpression`
  pre-filled with the mapped preset.

**`tests/QbitFlow.Tests/…` rules-API tests**

- Remove the tests covering the four deleted endpoints.
- Move any still-relevant assertions (rule compile, group serialisation) to
  cover `RuleWriter.ReconcileAsync` directly.

---

## Routing, links, cleanup

- **New:** `Pages/Pipeline.cshtml`, `Pages/Pipeline.cshtml.cs`,
  `Api/RuleWriter.cs`, `Pages/CpuSpeedMap.cs`.
- **Replace:** `Pages/PipelineEdit.cshtml(.cs)` and `Pages/Rules.cshtml(.cs)` —
  their contents are gutted and become bare `RedirectToPagePermanent` stubs
  pointing at `/Pipeline` (keeps bookmarks / `docs/` links alive for one
  release; removed after).
- **`Api/RulesApi.cs`:** drop the four mutation endpoints; helpers move to
  `RuleWriter`; `compile` + `test-rule` + `GET …/rules` stay.
- **Links:**
  - `Pages/Index.cshtml` — pipeline name links to `/Pipeline?id=`.
  - `Pages/Pipelines.cshtml` — replace the separate `Rules` and `Settings`
    buttons with one `Edit` → `/Pipeline?id=`; name link too.
- **Old URLs:** `/PipelineEdit` and `/Rules` — add two trivial Razor pages (or
  `RedirectToPagePermanent`) that 301 to `/Pipeline?id=` / `?pipelineId=` so
  bookmarks and the `docs/` links don't break. *Decision: keep as 301 redirect
  stubs for one release, then remove.*

---

## README

Under **Configuration → Access control** area (or a new short
**Pipelines** note):

- Pipelines are edited on one page (`/Pipeline?id=`) with Details / Sources /
  Rules / Fields sections and a single Save.
- Schedules are cron (6-field, seconds first); pick a preset or type your own.
- The schedule time zone follows the container's `TZ`.
- "CPU speed" (Low/Medium/High/SuperHigh) maps to the per-run torrent
  parallelism.

Trim the stale `AUTH_MODE` table row wording only if touched; otherwise leave
the rest of the README alone.

---

## Files touched

| Action | File |
|---|---|
| add | `src/QbitFlow.Web/Pages/Pipeline.cshtml` |
| add | `src/QbitFlow.Web/Pages/Pipeline.cshtml.cs` |
| add | `src/QbitFlow.Web/Api/RuleWriter.cs` |
| add | `src/QbitFlow.Web/Pages/CpuSpeedMap.cs` (static helper) |
| edit | `src/QbitFlow.Web/Api/RulesApi.cs` — drop 4 endpoints, use `RuleWriter` |
| edit | `src/QbitFlow.Web/Api/PipelinesApi.cs` — none functionally; comment `TimeZoneId` as no-op |
| edit | `src/QbitFlow.Engine/Scheduling/Schedule.cs` — `TimeZoneInfo.Local`, optional `tz` param |
| edit | `src/QbitFlow.Web/Pages/Pipelines.cshtml.cs` — create-seed → cron |
| edit | `src/QbitFlow.Web/Pages/Pipelines.cshtml` — one Edit link |
| edit | `src/QbitFlow.Web/Pages/Index.cshtml` — link target |
| replace | `src/QbitFlow.Web/Pages/PipelineEdit.cshtml(.cs)` → `RedirectToPagePermanent("/Pipeline")` stub |
| replace | `src/QbitFlow.Web/Pages/Rules.cshtml(.cs)` → `RedirectToPagePermanent("/Pipeline")` stub |
| edit | `tests/QbitFlow.Tests/Scheduling/ScheduleTests.cs` |
| add | `tests/QbitFlow.Tests/Web/PipelinePageTests.cs` |
| edit | rules-API tests — remove dead-endpoint cases, retarget to `RuleWriter` |
| edit | `README.md` |
| edit | `src/QbitFlow.Web/Program.cs` — register `RuleWriter` in DI |
