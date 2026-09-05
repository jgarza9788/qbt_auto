#!/usr/bin/env python3
"""Dump a font's character map, so icon codepoints are read from the font rather
than guessed. Nerd Font glyph names are source-prefixed (fa-sun_o, md-..., oct-...),
so search for a bare name like "sun" rather than anchoring the pattern.

Usage:
    python dump-cmap.py FONT.ttf                # every Private-Use-Area glyph
    python dump-cmap.py FONT.ttf sun moon gear  # only names containing these
"""
import sys
from fontTools.ttLib import TTFont


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2

    cmap = TTFont(sys.argv[1]).getBestCmap()
    needles = [s.lower() for s in sys.argv[2:]]

    count = 0
    for codepoint, name in sorted(cmap.items()):
        in_pua = (0xE000 <= codepoint <= 0xF8FF) or (0xF0000 <= codepoint <= 0xFFFFD)
        if needles:
            match = any(n in name.lower() for n in needles)
        else:
            match = in_pua
        if match:
            print(f"U+{codepoint:05X}\t{name}")
            count += 1

    print(f"\n{count} glyph(s)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
