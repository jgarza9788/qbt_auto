#!/usr/bin/env bash
# Regenerate the subsetted web font shipped at wwwroot/fonts/.
#
# Run from the repo root:  ./tools/fonts/subset-font.sh
# Prerequisites:           pip install fonttools brotli zopfli
#
# The ~2.4 MB source .ttf is NOT committed -- it is re-fetched from the pinned
# Nerd Fonts release each run. Only the derived .woff2 lives in the tree.
set -euo pipefail

NF_TAG=v3.4.0
SRC_NAME=JetBrainsMonoNerdFontMono-Regular.ttf
VERSION=v1                                  # bump on every regeneration (cache-busting)
OUT_DIR=src/Qbitflow.Web/wwwroot/fonts
OUT="${OUT_DIR}/jetbrainsmono-nerd-${VERSION}.woff2"

if [ ! -f tools/fonts/icons.txt ]; then
  echo "error: run this from the repository root" >&2
  exit 1
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

echo ">> fetching JetBrainsMono.zip @ ${NF_TAG}"
curl -fL --retry 2 -o "$TMP/JetBrainsMono.zip" \
  "https://github.com/ryanoasis/nerd-fonts/releases/download/${NF_TAG}/JetBrainsMono.zip"

echo ">> extracting ${SRC_NAME} + OFL.txt"
unzip -o -q "$TMP/JetBrainsMono.zip" "$SRC_NAME" "OFL.txt" -d "$TMP"

mkdir -p "$OUT_DIR"
cp "$TMP/OFL.txt" "$OUT_DIR/OFL.txt"

echo ">> subsetting"
# --unicodes-file      exact codepoints only; never a whole PUA range
# --layout-features='' strips calt/liga so '>=' '!=' '->' stay literal in the
#                      SQL editor and cron field, and drops dead lookup tables
# (hinting is deliberately KEPT: it measurably helps at 0.85rem on 1x Windows LCD)
# --name-IDs+=13,14    keeps the SIL OFL notice + URL inside the file itself
pyftsubset "$TMP/$SRC_NAME" \
  --output-file="$OUT" \
  --flavor=woff2 \
  --with-zopfli \
  --unicodes-file=tools/fonts/icons.txt \
  --layout-features='' \
  --no-layout-closure \
  --notdef-outline \
  --no-glyph-names \
  --name-IDs+=13,14 \
  --name-legacy \
  --recalc-bounds

echo ">> verifying every requested icon survived"
python - "$OUT" <<'PY'
import re, sys
from fontTools.ttLib import TTFont

wanted = []
for line in open("tools/fonts/icons.txt", encoding="utf-8"):
    line = line.split("#")[0].strip()
    m = re.fullmatch(r"U\+([0-9A-Fa-f]{4,6})", line)
    if m:
        wanted.append(int(m.group(1), 16))

cmap = TTFont(sys.argv[1]).getBestCmap()
missing = [cp for cp in wanted if cp not in cmap]
if missing:
    print("MISSING: " + ", ".join(f"U+{c:04X}" for c in missing), file=sys.stderr)
    raise SystemExit(1)
print(f"   all {len(wanted)} single-codepoint entries present; "
      f"{len(cmap)} glyphs total")
PY

echo ">> wrote $OUT ($(wc -c < "$OUT") bytes)"
echo
echo "Remember to bump the -${VERSION} reference in:"
echo "  - tools/fonts/subset-font.sh   (VERSION)"
echo "  - src/Qbitflow.Web/wwwroot/css/site.css        (@font-face src)"
echo "  - src/Qbitflow.Web/Pages/Shared/_Layout.cshtml (preload link)"
