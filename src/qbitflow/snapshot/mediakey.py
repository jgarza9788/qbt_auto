"""Reduce a release filename to something comparable with a library title.

``Some.Show.S01E04.2160p.WEB-DL.DDP5.1.HDR.x265-GROUP.mkv`` and Jellyfin's
``Some Show`` have to end up equal for a rule about that show to work. This does
that reduction, and it is the piece most worth getting right: everything
downstream in matching depends on it.

The rule that does the heavy lifting is the last one. Scene names put the title
first and the technical soup after, so once a known quality tag has been seen,
everything following it is release-group noise -- *except* a year or an episode
code, which are part of the identity and can legitimately trail the soup.
"""

from __future__ import annotations

import re

#: Tokens that mark the technical portion of a release name.
QUALITY_TAGS = frozenset(
    {
        # resolution
        "2160p", "1440p", "1080p", "1080i", "720p", "576p", "480p", "360p",
        "4k", "8k", "uhd", "fullhd", "hd", "sd",
        # source
        "bluray", "bdrip", "brrip", "bdremux", "remux", "bd25", "bd50",
        "web", "webrip", "webdl", "hdtv", "pdtv", "dsr", "dvdrip", "dvdr",
        "dvd", "dvd5", "dvd9", "hdrip", "hdcam", "cam", "camrip", "telesync",
        "telecine", "screener", "dvdscr", "r5", "workprint", "vodrip",
        "amzn", "nf", "hmax", "dsnp", "atvp", "hulu", "pcok", "stan", "ip",
        # codec
        "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx",
        "av1", "vp9", "mpeg2",
        # colour and bit depth
        "10bit", "8bit", "12bit", "hdr", "hdr10", "hdr10plus", "sdr",
        "dv", "dolbyvision", "hlg",
        # audio
        "aac", "aac2", "ac3", "eac3", "dd", "ddp", "dts", "dtshd", "dtsx",
        "truehd", "atmos", "flac", "mp3", "opus", "lpcm", "pcm",
        # edition and status
        "proper", "repack", "rerip", "extended", "unrated", "uncut",
        "remastered", "limited", "internal", "dubbed", "subbed", "subs",
        "multi", "dual", "complete", "hybrid", "imax", "criterion",
        "readnfo", "nfofix",
    }
)

_YEAR = re.compile(r"^(19|20)\d{2}$")
_EPISODE = re.compile(r"^s\d{1,3}e\d{1,4}$", re.IGNORECASE)
_SEASON = re.compile(r"^s\d{1,3}$", re.IGNORECASE)
_EXTENSION = re.compile(r"\.[a-z0-9]{2,4}$", re.IGNORECASE)
_BRACKETED = re.compile(r"\[[^\]]*\]|\{[^}]*\}")
#: Parenthesised groups, except a bare year, which is part of the identity.
_PARENS = re.compile(r"\((?!(?:19|20)\d{2}\))[^)]*\)")
#: Tags that scene names write with an internal separator. Joined back up before
#: separators become spaces, or "WEB-DL" arrives as the two tokens "web" and "dl"
#: and only the first is recognised.
_COMPOUND = (
    (re.compile(r"\bweb[.\-_ ]?dl\b"), "webdl"),
    (re.compile(r"\bblu[.\-_ ]?ray\b"), "bluray"),
    (re.compile(r"\bbd[.\-_ ]?remux\b"), "bdremux"),
    (re.compile(r"\bdts[.\-_ ]?hd(?:[.\-_ ]?ma)?\b"), "dtshd"),
    (re.compile(r"\btrue[.\-_ ]?hd\b"), "truehd"),
    (re.compile(r"\bdolby[.\-_ ]?vision\b"), "dolbyvision"),
    (re.compile(r"\bhdr[.\-_ ]?10(?:[.\-_ ]?plus)?\b"), "hdr10"),
    (re.compile(r"\b([xh])[.\-_ ]?26([45])\b"), r"\g<1>26\g<2>"),
    (re.compile(r"\bmulti[.\-_ ]?sub\b"), "subbed"),
)

#: Audio codec plus channel layout, e.g. "DDP5.1" or "EAC3 5.1". Collapsed to the
#: codec alone; the plain "\d[.\-]\d" form cannot be matched generically because
#: it also appears inside strings like "2019.1080p".
_AUDIO_CHANNELS = re.compile(
    r"\b(dd|ddp|dts|dtshd|truehd|eac3|ac3|aac|atmos|flac|opus|lpcm|pcm)"
    r"\+?\d?[.\-_ ]?\d[.\-]\d\b"
)
#: A standalone channel layout with nothing attached.
_BARE_CHANNELS = re.compile(r"(?<![\d.])\d[.\-]\d(?![\d.])")
_SEPARATORS = re.compile(r"[._\-+]+")
_WHITESPACE = re.compile(r"\s+")


def is_year(token: str) -> bool:
    return bool(_YEAR.match(token))


def is_episode_code(token: str) -> bool:
    return bool(_EPISODE.match(token) or _SEASON.match(token))


def _leaf(path: str) -> str:
    """Last path segment, whichever separator was used."""
    if not path:
        return ""
    return re.split(r"[\\/]", path.strip())[-1]


def _clean(text: str) -> str:
    text = _EXTENSION.sub("", text)
    text = text.lower()
    text = _BRACKETED.sub(" ", text)
    text = _PARENS.sub(" ", text)
    text = text.replace("(", " ").replace(")", " ")
    for pattern, replacement in _COMPOUND:
        text = pattern.sub(replacement, text)
    text = _AUDIO_CHANNELS.sub(r"\1", text)
    text = _BARE_CHANNELS.sub(" ", text)
    text = _SEPARATORS.sub(" ", text)
    text = re.sub(r"[^a-z0-9 ]+", " ", text)
    return _WHITESPACE.sub(" ", text).strip()


def normalize_filename(path: str) -> str:
    """The comparable form of a release filename."""
    tokens = _clean(_leaf(path)).split()
    if not tokens:
        return ""

    last_quality = -1
    for index, token in enumerate(tokens):
        if token in QUALITY_TAGS:
            last_quality = index

    if last_quality >= 0:
        head = tokens[:last_quality]
        # Past the technical soup, only identity-bearing tokens survive.
        tail = [t for t in tokens[last_quality + 1 :] if is_year(t) or is_episode_code(t)]
        tokens = head + tail

    tokens = [t for t in tokens if t not in QUALITY_TAGS]
    return " ".join(tokens)


def normalize_title(text: str) -> str:
    """A library title reduced the same way, and truncated at the year or episode code.

    ``The Show 2019 S01E02`` and ``The Show`` both reduce to ``the show``, which is
    what lets an episode file match its series entry.
    """
    tokens = _clean(_leaf(text)).split()
    out: list[str] = []
    for token in tokens:
        if is_year(token) or is_episode_code(token):
            break
        if token in QUALITY_TAGS:
            continue
        out.append(token)
    return " ".join(out)


def extract_title_year(text: str) -> tuple[str, int | None]:
    """Split a release name into its title and year, where one is present."""
    tokens = _clean(_leaf(text)).split()
    title: list[str] = []
    year: int | None = None
    for token in tokens:
        if is_year(token):
            year = int(token)
            break
        if is_episode_code(token):
            break
        if token in QUALITY_TAGS:
            continue
        title.append(token)
    return " ".join(title), year


def match_key(media_type: str, title: str, year: int | None) -> str:
    """Cross-source identity for a library item.

    Type, normalized title and year -- so the same film reported by Plex and by
    Jellyfin collapses to one row whose watch counts are the sum of both.
    """
    normalized = normalize_title(title)
    return f"{media_type}|{normalized}|{year or ''}"


def normalize_segments(path: str, count: int = 2) -> str:
    """The trailing segments of a path, each normalized and rejoined.

    Used by the path-segment matching strategy, where a torrent's containing
    folder is the thing that resembles the library's folder.
    """
    if not path:
        return ""
    parts = [p for p in re.split(r"[\\/]", path.strip()) if p]
    tail = parts[-count:]
    return "/".join(normalize_filename(p) for p in tail)
