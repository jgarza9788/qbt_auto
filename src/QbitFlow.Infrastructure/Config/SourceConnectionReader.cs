using System.Text;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Infrastructure.Config;

/// <summary>A <see cref="SourceConnection"/> with its secret decrypted and env-var overrides applied.</summary>
public sealed record ResolvedConnection(
    Guid Id,
    string Name,
    SourceKind Kind,
    string BaseUrl,
    SourceAuthMode AuthMode,
    string Username,
    string Secret,
    string OptionsJson);

/// <summary>
/// Loads a connection from the DB, decrypts its secret, then overlays environment variables so
/// secrets can stay out of the volume:
///  <c>SOURCE__&lt;NAME&gt;__BASEURL | USERNAME | SECRET</c>, plus shortcuts
///  <c>QBT_URL/QBT_USER/QBT_PWD</c> and <c>PLEX_URL/PLEX_USER/PLEX_PWD/PLEX_TOKEN</c>.
/// Env always wins and is never persisted.
/// </summary>
public sealed class SourceConnectionReader(IDbContextFactory<AppDbContext> dbFactory, ISecretProtector secrets)
{
    public async Task<ResolvedConnection> ResolveAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var c = await db.SourceConnections.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException($"Source connection {id} not found.");

        var secret = c.SecretCiphertext is { } cipher && c.SecretNonce is { } nonce
            ? secrets.Unprotect(cipher, nonce)
            : "";

        var envName = Sanitize(c.Name);
        var baseUrl = Env($"SOURCE__{envName}__BASEURL") ?? Shortcut(c.Kind, "URL") ?? c.BaseUrl;
        var username = Env($"SOURCE__{envName}__USERNAME") ?? Shortcut(c.Kind, "USER") ?? c.Username ?? "";
        var resolvedSecret = Env($"SOURCE__{envName}__SECRET")
            ?? Shortcut(c.Kind, c.AuthMode == SourceAuthMode.PlexToken ? "TOKEN" : "PWD")
            ?? secret;

        return new ResolvedConnection(c.Id, c.Name, c.Kind, baseUrl, c.AuthMode, username, resolvedSecret, c.OptionsJson);
    }

    private static string? Shortcut(SourceKind kind, string suffix) => kind switch
    {
        SourceKind.Qbt => Env($"QBT_{suffix}"),
        SourceKind.Plex => Env($"PLEX_{suffix}"),
        SourceKind.Jellyfin => Env($"JELLYFIN_{suffix}"),
        _ => null,
    };

    private static string? Env(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToUpperInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }
}
