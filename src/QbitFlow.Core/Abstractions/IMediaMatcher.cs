using QbitFlow.Core.Contracts;

namespace QbitFlow.Core.Abstractions;

public sealed record MediaMatch(Guid MediaItemId, double Confidence, string Strategy);

/// <summary>One media library item, flattened for matching.</summary>
public sealed record CatalogEntry(
    Guid MediaItemId,
    string Title,
    string MediaType,
    int? Year,
    IReadOnlyList<CatalogFile> Files);

public sealed record CatalogFile(string NormalizedFileName, string ParentDirNormalized, long? SizeBytes);

/// <summary>Indexed view over the media catalog for fast lookups by the matcher.</summary>
public interface IMediaCatalog
{
    IReadOnlyList<CatalogEntry> ByFileName(string normalizedFileName);
    IReadOnlyList<CatalogEntry> ByTitle(string normalizedTitle);
    IReadOnlyList<CatalogEntry> All { get; }
}

/// <summary>Correlates a torrent with a media library item. Pluggable; v1 is filename-first.</summary>
public interface IMediaMatcher
{
    MediaMatch? Match(TorrentView torrent, IMediaCatalog catalog);
}
