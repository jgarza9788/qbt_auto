namespace Qbitflow.Core.Domain;

/// <summary>
/// Rules for the identifiers that appear inside a source-data field key
/// (<c>&lt;type&gt;.&lt;instance&gt;.&lt;field&gt;</c>, e.g. <c>jellyfin.jellyfin1.title</c>).
/// An instance name is one segment of that key, so it cannot contain the '.' separator
/// or the '*' wildcard -- hence the validation applied to instance and storage-path names.
/// </summary>
public static class SourceNaming
{
    /// <summary>The instance segment that means "any instance of this type".</summary>
    public const string Wildcard = "*";

    /// <summary>
    /// Characters allowed in an instance / storage-path name. Excludes '.' and '*' because they are
    /// the key's separator and wildcard, and excludes whitespace so a key stays a single token in
    /// hand-written advanced SQL.
    /// </summary>
    public const string NamePattern = @"^[A-Za-z0-9][A-Za-z0-9_-]*$";

    public const string NameValidationMessage =
        "Name must start with a letter or digit and contain only letters, digits, hyphens and underscores -- it becomes a segment of field keys like jellyfin.<name>.title.";

    /// <summary>The lowercase type segment for a source type, e.g. SourceType.Jellyfin -> "jellyfin".</summary>
    public static string TypeKey(SourceType type) => type.ToString().ToLowerInvariant();

    /// <summary>The pseudo-type used for configured storage paths, which are not <see cref="Instance"/>s.</summary>
    public const string StorageTypeKey = "storage";

    /// <summary>The source type every rule anchors on: a rule always resolves to a set of qBittorrent torrents.</summary>
    public const SourceType AnchorType = SourceType.Qbittorrent;

    /// <summary>Every source type that contributes media-library items and/or watch events, i.e. everything but the anchor.</summary>
    public static IEnumerable<SourceType> MediaHistoryTypes =>
        Enum.GetValues<SourceType>().Where(t => t != AnchorType);
}
