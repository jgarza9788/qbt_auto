using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Matching;

namespace QbitFlow.Engine.Matching;

/// <summary>In-memory index over the media catalog, built once per analytics refresh / pipeline run.</summary>
public sealed class MediaCatalog : IMediaCatalog
{
    private readonly Dictionary<string, List<CatalogEntry>> _byFileName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CatalogEntry>> _byTitle = new(StringComparer.Ordinal);
    private readonly List<CatalogEntry> _all = [];

    public IReadOnlyList<CatalogEntry> All => _all;

    public IReadOnlyList<CatalogEntry> ByFileName(string normalizedFileName) =>
        normalizedFileName.Length > 0 && _byFileName.TryGetValue(normalizedFileName, out var list) ? list : [];

    public IReadOnlyList<CatalogEntry> ByTitle(string normalizedTitle) =>
        normalizedTitle.Length > 0 && _byTitle.TryGetValue(normalizedTitle, out var list) ? list : [];

    public static MediaCatalog Build(IEnumerable<MediaItem> items)
    {
        var catalog = new MediaCatalog();

        foreach (var item in items)
        {
            var files = item.Files
                .Select(f => new CatalogFile(
                    MediaKey.NormalizeFileName(f.FileName),
                    ParentDirNormalized(f.Path),
                    f.SizeBytes))
                .ToArray();

            var entry = new CatalogEntry(item.Id, item.Title, item.MediaType, item.Year, files);
            catalog._all.Add(entry);

            foreach (var f in files)
                if (f.NormalizedFileName.Length > 0)
                    Add(catalog._byFileName, f.NormalizedFileName, entry);

            var titleNorm = MediaKey.NormalizeTitle(item.Title);
            if (titleNorm.Length > 0)
                Add(catalog._byTitle, titleNorm, entry);
        }

        return catalog;
    }

    private static void Add(Dictionary<string, List<CatalogEntry>> map, string key, CatalogEntry entry)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(entry)) list.Add(entry);
    }

    private static string ParentDirNormalized(string path)
    {
        var p = path.Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        if (slash <= 0) return "";
        var parent = p[..slash];
        var pslash = parent.LastIndexOf('/');
        var lastSeg = pslash >= 0 ? parent[(pslash + 1)..] : parent;
        return MediaKey.NormalizeFileName(lastSeg);
    }
}
