using Qbitflow.Core.Domain;

namespace Qbitflow.Engine.Conditions;

/// <summary>
/// A parsed <c>&lt;type&gt;.&lt;instance&gt;.&lt;field&gt;</c> reference, e.g.
/// <c>jellyfin.jellyfin1.title</c>. This is the only way a condition names data: the type says
/// which snapshot table, the instance says which configured source within it (or <c>*</c> for any),
/// and the field says which documented column or aggregate.
/// </summary>
public readonly record struct FieldKey(string Type, string Instance, string Field)
{
    public const string Shape = "<type>.<instance>.<field>";

    public bool IsWildcardInstance => Instance == SourceNaming.Wildcard;

    /// <summary>The two-segment source reference this key belongs to, e.g. "jellyfin.jellyfin1".</summary>
    public string Source => $"{Type}.{Instance}";

    public override string ToString() => $"{Type}.{Instance}.{Field}";

    public static bool TryParse(string? raw, out FieldKey key, out string? error)
    {
        key = default;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"Field key is empty; expected {Shape} (e.g. qbittorrent.*.category).";
            return false;
        }

        var parts = raw.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
        {
            error = $"Field key '{raw}' is invalid; expected {Shape} (e.g. qbittorrent.*.category).";
            return false;
        }

        key = new FieldKey(parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>Parses a two-segment source reference (an EXISTS node's Source), e.g. "jellyfin.*".</summary>
    public static bool TryParseSource(string? raw, out string type, out string instance, out string? error)
    {
        type = instance = string.Empty;
        error = null;

        var parts = (raw ?? string.Empty).Split('.');
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty))
        {
            error = $"Source '{raw}' is invalid; expected <type>.<instance> (e.g. jellyfin.jellyfin1 or jellyfin.*).";
            return false;
        }

        type = parts[0];
        instance = parts[1];
        return true;
    }
}
