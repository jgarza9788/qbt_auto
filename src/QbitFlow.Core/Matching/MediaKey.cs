using System.Text.RegularExpressions;

namespace QbitFlow.Core.Matching;

/// <summary>Filename / title normalisation used for matching torrents to media library items.</summary>
public static partial class MediaKey
{
    [GeneratedRegex(@"\.[a-z0-9]{2,4}$", RegexOptions.IgnoreCase)] private static partial Regex Extension();
    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")] private static partial Regex Brackets();
    [GeneratedRegex(@"\((?!(?:19|20)\d{2}\))[^)]*\)")] private static partial Regex ParensExceptYear();
    [GeneratedRegex(@"[._]+")] private static partial Regex Separators();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"\b(19|20)\d{2}\b")] private static partial Regex Year();
    [GeneratedRegex(@"\bS\d{1,2}E\d{1,3}\b", RegexOptions.IgnoreCase)] private static partial Regex Episode();

    private static readonly string[] Tags =
    [
        "2160p", "1080p", "720p", "480p", "4k", "uhd",
        "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx",
        "bluray", "blu-ray", "brrip", "bdrip", "webrip", "web-dl", "webdl", "web", "hdtv", "dvdrip", "dvd", "remux", "hdrip",
        "hdr", "hdr10", "dv", "dolby", "vision", "sdr",
        "dts", "dts-hd", "truehd", "atmos", "aac", "ac3", "eac3", "flac", "mp3", "5.1", "7.1", "2.0",
        "proper", "repack", "internal", "limited", "extended", "unrated", "remastered", "criterion",
        "cam", "hdcam", "ts", "hdts", "telesync", "telecine", "tc", "scr", "screener",
        "multi", "dual", "subbed", "dubbed", "10bit", "8bit",
    ];

    /// <summary>Lower-cased, extension-stripped, tag-stripped, whitespace-collapsed form of a file name.</summary>
    public static string NormalizeFileName(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return "";

        var name = nameOrPath.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        name = Extension().Replace(name, "");
        name = name.ToLowerInvariant();
        name = Brackets().Replace(name, " ");
        name = ParensExceptYear().Replace(name, " ");
        name = Separators().Replace(name, " ");
        name = name.Replace('-', ' ');

        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Everything after the last quality tag is release-group / muxer cruft — drop it,
        // but keep a trailing year or SxxExx.
        var lastTag = -1;
        for (var i = 0; i < tokens.Length; i++)
            if (Tags.Contains(tokens[i], StringComparer.Ordinal)) lastTag = i;

        var kept = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            if (Tags.Contains(tok, StringComparer.Ordinal)) continue;
            if (i > lastTag && lastTag >= 0 && !IsYearOrEpisode(tok)) continue;
            kept.Add(tok);
        }

        return MultiSpace().Replace(string.Join(' ', kept), " ").Trim();
    }

    /// <summary>Just the leading title portion (drops anything from the year / SxxExx onward).</summary>
    public static string NormalizeTitle(string? nameOrPath)
    {
        var norm = NormalizeFileName(nameOrPath);
        if (norm.Length == 0) return "";

        var cut = norm.Length;
        var y = Year().Match(norm);
        if (y.Success) cut = Math.Min(cut, y.Index);
        var e = Episode().Match(norm);
        if (e.Success) cut = Math.Min(cut, e.Index);

        return norm[..cut].Trim();
    }

    /// <summary>Best-effort (title, year) extraction from a torrent name.</summary>
    public static (string Title, int? Year) ExtractTitleYear(string? name)
    {
        var norm = NormalizeFileName(name);
        int? year = null;
        var y = Year().Match(norm);
        if (y.Success && int.TryParse(y.Value, out var yv)) year = yv;
        return (NormalizeTitle(name), year);
    }

    private static bool IsYearOrEpisode(string token) =>
        Year().IsMatch(token) || Episode().IsMatch(token);

    /// <summary>Last one-or-two path segments of a directory-ish path, normalised.</summary>
    public static string NormalizeLastSegments(string? path, int segments = 2)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tail = parts.TakeLast(segments);
        return NormalizeFileName(string.Join(' ', tail));
    }
}
