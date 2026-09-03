# Migrating from `qbt_auto` to `qbit-flow`

The `qbt_auto` console app (cron + `config.json`) is retired. qbit-flow is a long-running container
with a web UI and a database. Your rules carry over.

## First-boot import

Point `CONFIG_IMPORT_PATH` at your old `config.json` (JSON5 — comments and trailing commas are fine):

```yaml
environment:
  - CONFIG_IMPORT_PATH=/config/config.json
volumes:
  - ./config:/config:ro
```

On the first start (and only when no pipeline exists yet) qbit-flow:

* creates a **qBittorrent** source from the `qbt` block and a **Plex** source from the `plex` block
  (Jellyfin has no equivalent in the old config — add it in the UI);
* creates one pipeline, **"Imported (config.json)"**, **disabled** and in **dry-run**, wired to that
  qBittorrent source as both data and action target;
* turns each `AutoTorrentRules[i]` into a rule whose **criteria string is kept verbatim** (Raw mode),
  so evaluation is identical, plus one action:

| Old `Type` | New action | Params |
|---|---|---|
| `AutoTag` | `tag.sync` | `{ tag }` — still adds on match / removes when it stops matching |
| `AutoCategory` | `category.set` | `{ category }` |
| `AutoMove` | `torrent.move` | `{ path }` |
| `AutoScript` | `script.run` | `{ runDir, shebang, script, timeout }` |
| `AutoSpeed` | `speed.limit` | `{ uploadKb, downloadKb }` (both `0` still pauses; `UownloadSpeed` typo tolerated) |

The import is idempotent (SHA-256 gated) — re-importing the same file is a no-op. Re-run it any time
from **Sources → Import**, with *force* to bypass the "pipelines already exist" guard.

## After import

1. Open the imported pipeline, review the rules, set a real schedule (interval or cron, min 5 min).
2. Leave **dry-run** on for a run or two and watch the run log — it reports every "would apply".
3. Turn dry-run off and enable the pipeline.

## Field name changes

* Torrent, drive (`<mount>_FreeSizeGB`, `<mount>_PercentUsed`, …) and `daysAgo(...)` behave exactly as
  before — there is a golden parity test over the shipped `exampleConfig.json` criteria.
* `<ActiveTime>` is still in .NET **ticks** (so `<ActiveTime>/864000000000 >= 14.0` still means "14
  days"). New friendlier fields: `<ActiveTimeSeconds>`, `<ActiveTimeDays>`.
* Media/analytics fields are new and cache-backed:
  * `<hotcold>` — 0..1, quantile of the recency-weighted watch total **within the torrent's
    qBittorrent category** across all Plex + Jellyfin sources.
  * `<watch_total>`, `<days_since_last_watched>` (99999 if never), `<is_media_matched>`.
  * `<media_title>` / `<media_year>` / `<media_rating>` / `<media_genres>` / `<media_type>`.
  * `plex_*` aliases: `<plex_nview>` → `<hotcold>`; `<plex_nview_legacy>` is the old per-title,
    per-media-type quantile (single global bucket, closest to `qbt_auto` semantics);
    `<plex_viewCount>` → the rounded weighted watch total.
* Media matching is filename-first (normalised filename → path segment → title + year). Torrents the
  matcher can't place show up on the **Analytics → unmatched** list; tune library / release naming or
  use `<is_media_matched>` to guard those rules.
