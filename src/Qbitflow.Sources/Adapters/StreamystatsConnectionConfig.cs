using System.Text.Json;
using Npgsql;
using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Sources.Adapters;

/// <summary>
/// Turns one instance's settings into an Npgsql connection to the Streamystats database.
///
/// Streamystats is addressed by database rather than by HTTP because its REST API cannot serve
/// this data: the only endpoint carrying playback history is gated behind a browser session
/// cookie, and even that payload has no file path, which is what a playback event has to carry to
/// be correlated to a torrent. The database has both.
///
/// Credentials come from the instance's Username/Password fields, which are encrypted at rest.
/// Any user:password in the base URL is deliberately ignored -- BaseUrl is stored in the clear.
/// </summary>
public sealed class StreamystatsConnectionConfig
{
    public const string DefaultDatabase = "streamystats";
    public const int DefaultPort = 5432;
    public const int DefaultMaxRows = 50_000;

    public required string ConnectionString { get; init; }

    /// <summary>Which Streamystats server to read, or null for every server in the database.</summary>
    public int? ServerId { get; init; }

    public int MaxRows { get; init; } = DefaultMaxRows;

    /// <summary>True when the base URL carried credentials, so the caller can say they were ignored.</summary>
    public bool BaseUrlHadCredentials { get; init; }

    public static StreamystatsConnectionConfig Parse(SourceConnectionInfo connection)
    {
        var (host, port, database, hadCredentials) = ParseBaseUrl(connection.BaseUrl);

        int? serverId = null;
        var maxRows = DefaultMaxRows;
        string? sslModeOverride = null;

        if (!string.IsNullOrWhiteSpace(connection.ExtraConfigJson))
        {
            using var doc = JsonDocument.Parse(connection.ExtraConfigJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("database", out var db) && db.ValueKind == JsonValueKind.String)
            {
                database = db.GetString() ?? database;
            }
            if (root.TryGetProperty("serverId", out var sid) && sid.ValueKind == JsonValueKind.Number)
            {
                serverId = sid.GetInt32();
            }
            if (root.TryGetProperty("maxRows", out var max) && max.ValueKind == JsonValueKind.Number)
            {
                maxRows = Math.Clamp(max.GetInt32(), 1, 1_000_000);
            }
            if (root.TryGetProperty("sslMode", out var ssl) && ssl.ValueKind == JsonValueKind.String)
            {
                sslModeOverride = ssl.GetString();
            }
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = string.IsNullOrWhiteSpace(connection.Username) ? "postgres" : connection.Username,
            Password = connection.Password ?? string.Empty,
            Timeout = Math.Clamp(connection.TimeoutSeconds, 1, 300),
            CommandTimeout = Math.Clamp(connection.TimeoutSeconds, 1, 300),

            // Read-only: the adapter only ever SELECTs, and saying so lets PostgreSQL reject a
            // write outright rather than trusting this code to never issue one.
            Options = "-c default_transaction_read_only=on",

            // One short-lived connection per refresh; pooling across refreshes would hold a
            // connection open against someone else's database for no benefit.
            Pooling = false
        };

        // Prefer negotiates TLS when the server offers it and stays plain when it doesn't, which is
        // what a Postgres on a private container network almost always needs. Note this ignores the
        // instance's "Verify SSL certificate" box: that maps onto an HTTPS handler, and Npgsql's
        // equivalent (VerifyFull) hard-fails against a server with no TLS configured at all, which
        // is the normal case here. Certificate validation is opt-in per instance instead:
        //   {"sslMode": "VerifyFull"}
        builder.SslMode = sslModeOverride is not null
            && Enum.TryParse<SslMode>(sslModeOverride, ignoreCase: true, out var mode)
            ? mode
            : SslMode.Prefer;

        return new StreamystatsConnectionConfig
        {
            ConnectionString = builder.ConnectionString,
            ServerId = serverId,
            MaxRows = maxRows,
            BaseUrlHadCredentials = hadCredentials
        };
    }

    /// <summary>
    /// Accepts what people actually have to hand: a bare host, "host:5432", or the whole
    /// DATABASE_URL from their compose file ("postgresql://user:pass@vectorchord:5432/streamystats").
    /// </summary>
    private static (string Host, int Port, string Database, bool HadCredentials) ParseBaseUrl(string baseUrl)
    {
        var raw = (baseUrl ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw new InvalidOperationException(
                "Base URL is required: the Streamystats database host, e.g. \"vectorchord:5432\".");
        }

        if (raw.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"Base URL '{raw}' is not a valid URL.");
            }

            var db = uri.AbsolutePath.Trim('/');
            return (
                uri.Host,
                uri.Port > 0 ? uri.Port : DefaultPort,
                db.Length > 0 ? db : DefaultDatabase,
                !string.IsNullOrEmpty(uri.UserInfo));
        }

        var parts = raw.Split(':', 2);
        var host = parts[0].Trim();
        if (host.Length == 0)
        {
            throw new InvalidOperationException($"Base URL '{raw}' has no host.");
        }

        if (parts.Length == 1)
        {
            return (host, DefaultPort, DefaultDatabase, false);
        }

        if (!int.TryParse(parts[1].Trim(), out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Base URL '{raw}' has an invalid port.");
        }

        return (host, port, DefaultDatabase, false);
    }
}
