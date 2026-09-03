using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Core.Matching;

namespace QbitFlow.Engine.Matching;

/// <summary>
/// Ordered strategy chain — the first strategy that produces a candidate wins; the size hint only
/// disambiguates between candidates of the same strategy. No fuzzy matching in v1.
/// </summary>
public sealed class FilenameMediaMatcher : IMediaMatcher
{
    public MediaMatch? Match(TorrentView torrent, IMediaCatalog catalog)
    {
        var contentName = LeafOf(torrent.ContentPath);
        var fnNorm = MediaKey.NormalizeFileName(contentName);
        var nameNorm = MediaKey.NormalizeFileName(torrent.Name);

        // 1) exact normalised filename
        var exact = catalog.ByFileName(fnNorm).Concat(catalog.ByFileName(nameNorm)).Distinct().ToArray();
        if (exact.Length > 0)
            return new MediaMatch(Pick(exact, torrent).MediaItemId, 1.0, "filename");

        // 2) path-segment
        var seg = MediaKey.NormalizeLastSegments(
            string.IsNullOrEmpty(torrent.ContentPath) ? torrent.SavePath : torrent.ContentPath, 2);
        if (seg.Length > 3)
        {
            var bySeg = catalog.All
                .Where(e => e.Files.Any(f => f.ParentDirNormalized.Length > 3 &&
                                             (seg.Contains(f.ParentDirNormalized) || f.ParentDirNormalized.Contains(seg))))
                .ToArray();
            if (bySeg.Length > 0)
                return new MediaMatch(Pick(bySeg, torrent).MediaItemId, 0.8, "path-segment");
        }

        // 3) title + year
        var (title, year) = MediaKey.ExtractTitleYear(torrent.Name);
        if (title.Length > 2)
        {
            var byTitle = catalog.ByTitle(title);
            var yearMatched = year is { } y ? byTitle.Where(e => e.Year == y).ToArray() : [];
            var candidates = yearMatched.Length > 0 ? yearMatched : byTitle.ToArray();
            if (candidates.Length > 0)
            {
                var conf = yearMatched.Length > 0 ? 0.7 : 0.6;
                return new MediaMatch(Pick(candidates, torrent).MediaItemId, conf, "title-year");
            }
        }

        return null;
    }

    /// <summary>Size-hint tie-breaker: prefer a candidate with a file within ±2% of the torrent size.</summary>
    private static CatalogEntry Pick(IReadOnlyList<CatalogEntry> candidates, TorrentView torrent)
    {
        if (candidates.Count == 1 || torrent.Size <= 0) return candidates[0];

        var tol = torrent.Size * 0.02;
        var best = candidates
            .Select(e => (Entry: e, Delta: e.Files
                .Where(f => f.SizeBytes is > 0)
                .Select(f => Math.Abs(f.SizeBytes!.Value - torrent.Size))
                .DefaultIfEmpty(long.MaxValue)
                .Min()))
            .OrderBy(x => x.Delta)
            .First();

        return best.Delta <= tol ? best.Entry : candidates[0];
    }

    private static string LeafOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.Replace('\\', '/').TrimEnd('/');
        var slash = p.LastIndexOf('/');
        return slash >= 0 ? p[(slash + 1)..] : p;
    }
}
