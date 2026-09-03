namespace QbitFlow.Core.Domain;

/// <summary>
/// One configured instance of any source type. <see cref="SourceKind.Qbt"/> rows are both a data
/// source and an action target.
/// </summary>
public class SourceConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; }
    public string BaseUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public SourceAuthMode AuthMode { get; set; } = SourceAuthMode.UserPassword;
    public string? Username { get; set; }

    /// <summary>Protected secret (password / API key / token). Never logged, never returned to the UI.</summary>
    public byte[]? SecretCiphertext { get; set; }
    public byte[]? SecretNonce { get; set; }

    /// <summary>Kind-specific options as JSON (Plex clientId; Jellyfin userScope; Qbt verifyTls, httpTimeoutSec).</summary>
    public string OptionsJson { get; set; } = "{}";

    public HealthState HealthState { get; set; } = HealthState.Unknown;
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public string? LastError { get; set; }
    public int? LatencyMs { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
