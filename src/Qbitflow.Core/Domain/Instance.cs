namespace Qbitflow.Core.Domain;

/// <summary>
/// A single named connection to a data source (qBittorrent, Plex, Jellyfin, Tautulli,
/// Jellystat, Jellyglance). Multiple instances of the same SourceType are supported.
/// Credential fields are stored encrypted at rest (see ISecretProtector) and are only
/// decrypted in memory when an adapter needs to make a request.
/// </summary>
public class Instance
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public SourceType SourceType { get; set; }
    public required string BaseUrl { get; set; }

    /// <summary>Encrypted API key / token, if this source uses one.</summary>
    public string? ApiKeyProtected { get; set; }

    public string? Username { get; set; }

    /// <summary>Encrypted password, if this source uses username/password auth.</summary>
    public string? PasswordProtected { get; set; }

    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public bool VerifySsl { get; set; } = true;

    /// <summary>Source-specific extra settings as a JSON object (e.g. Plex machine identifier).</summary>
    public string? ExtraConfigJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
