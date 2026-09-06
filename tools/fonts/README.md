# Web font tooling

Generates the subsetted icon + monospace font shipped at
`src/Qbitflow.Web/wwwroot/fonts/`.

Everything in this directory is **build-time only**. The `Dockerfile` copies
just `src/`, so nothing here reaches the runtime image, and none of it is
served over HTTP.

## What ships

| File | Notes |
|---|---|
| `wwwroot/fonts/jetbrainsmono-nerd-v2.woff2` | ~16 KB subset. Committed. |
| `wwwroot/fonts/OFL.txt` | SIL OFL 1.1, copied verbatim from the release. |
| `wwwroot/fonts/LICENSE-nerdfonts.txt` | Provenance + upstream attributions. |

The ~2.4 MB source `.ttf` is **not** committed — `subset-font.sh` re-fetches it
from the pinned release each run.

## Prerequisites

```bash
pip install fonttools brotli zopfli
```

`brotli` is required for `--flavor=woff2`; `zopfli` enables `--with-zopfli`.

## Regenerating

From the repository root:

```bash
./tools/fonts/subset-font.sh
```

The script fetches the pinned `NF_TAG`, extracts
`JetBrainsMonoNerdFontMono-Regular.ttf`, subsets it, and then verifies that
every codepoint listed in `icons.txt` actually survived into the output —
it exits non-zero if any is missing.

### Why the `Mono` variant

Nerd Fonts ships three widths. `NerdFontMono` forces every glyph — icons
included — into a single character cell. This font drives the cron `<input>`
and the advanced-SQL `<textarea>`, where column alignment matters, so the
single-cell variant is the correct one. The plain `NerdFont` variant lets
icons run ~1.5 cells wide and would break alignment.

### Why ligatures are off

`--layout-features=''` strips `calt`/`liga`. JetBrains Mono would otherwise
fuse `>=`, `<=`, `!=` and `->` into single glyphs. In a SQL editor that is
actively misleading — `active_days >= 14` must read as typed.

## Adding an icon

1. Find its codepoint **in the font** (never guess — Nerd Font glyph names are
   source-prefixed, e.g. `fa-sun_o`, so search for the bare word):

   ```bash
   python tools/fonts/dump-cmap.py /path/to/JetBrainsMonoNerdFontMono-Regular.ttf gear folder
   ```

   With no search terms it dumps every Private-Use-Area glyph.

2. Add a line to `icons.txt`, recording the glyph name in the comment:

   ```
   U+F013        # fa-gear -> settings
   ```

3. Bump `VERSION` in `subset-font.sh` (`v1` → `v2`) — `@font-face` `src` in a
   static stylesheet cannot be cache-busted by `asp-append-version`, so the
   filename carries the version instead.

4. Re-run `./tools/fonts/subset-font.sh`.

5. Add the class in `wwwroot/css/site.css`:

   ```css
   .qf-icon--gear::before { content: "\f013"; }
   ```

6. Update the two `-vN` references the script prints on completion.

Prefer Font Awesome codepoints (`fa-*`, U+F000–U+F2FF). They are four hex
digits, so the CSS `content` escape is unambiguous. Material Design icons
(`md-*`) are five digits and need padding to six (`"\0f05a9"`) or a trailing
space so the CSS parser doesn't swallow the next character.

## Currently included

| Codepoint | Glyph | CSS class | Used for |
|---|---|---|---|
| U+F185 | `fa-sun_o` | `--sun` | theme: light |
| U+F186 | `fa-moon_o` | `--moon` | theme: dark |
| U+F042 | `fa-circle_half_stroke` | `--auto` | theme: auto / match system |
| U+F0C5 | `fa-files_o` | `--copy` | copy |
| U+F00C | `fa-check` | `--check` | success / validate |
| U+F00D | `fa-xmark` | `--close` | close / remove / clear |
| U+F078 | `fa-chevron_down` | `--chevron` | disclosure |
| U+F1C0 | `fa-database` | `--database` | field reference / SQL |
| U+F017 | `fa-clock_o` | `--clock` | schedule / history |
| U+F04B | `fa-play` | `--play` | run now / dry run |
| U+F071 | `fa-warning` | `--warning` | validation error |
| U+F02B | `fa-tag` | `--tag` | tags |
| U+F07B | `fa-folder` | `--folder` | paths / storage |
| U+F013 | `fa-gear` | `--gear` | settings / manage |
| U+F044 | `fa-pencil_square_o` | `--edit` | edit |
| U+F014 | `fa-trash_o` | `--delete` | delete |
| U+F067 | `fa-plus` | `--add` | add / create |
| U+F0C7 | `fa-save` | `--save` | save |
| U+F0B0 | `fa-filter` | `--filter` | filter |
| U+F019 | `fa-download` | `--download` | export |
| U+F093 | `fa-upload` | `--upload` | import |
| U+F06E | `fa-eye` | `--eye` | preview |
| U+F2F1 | `fa-rotate` | `--rotate` | test / switch mode |
| U+F0E4 | `fa-tachometer` | `--dashboard` | dashboard |
| U+F0AE | `fa-tasks` | `--rules` | rules |
| U+F1E6 | `fa-plug` | `--plug` | instances / connection |
| U+F233 | `fa-server` | `--server` | server / snapshot |
| U+F084 | `fa-key` | `--key` | password |
| U+F2F5 | `fa-right_from_bracket` | `--signout` | log out |
| U+F2F6 | `fa-right_to_bracket` | `--signin` | log in |

Every one is wired to a `.qf-icon--<name>` class in `site.css`. Three
(`server`, `tag`, `warning`) are defined but not yet used by any view --
they are pre-included so the next feature is a CSS-only change.
