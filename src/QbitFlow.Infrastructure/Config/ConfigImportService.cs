using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json5Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Infrastructure.Config;

public enum ImportMode { FirstBootOnly, Force }

public sealed record ImportResult(bool Imported, string Reason, int Sources, int Rules)
{
    public static ImportResult Skipped(string reason) => new(false, reason, 0, 0);
}

/// <summary>
/// Imports a legacy JSON5 <c>config.json</c> into the DB: <c>qbt</c>/<c>plex</c> → source connections,
/// each <c>AutoTorrentRules[i]</c> → a raw-mode <see cref="Rule"/> (verbatim <c>Criteria</c>) plus one
/// <see cref="RuleAction"/>. Idempotent: a re-import of the same file is a no-op.
/// </summary>
public sealed class ConfigImportService(
    AppDbContext db,
    ISecretProtector secrets,
    ILogger<ConfigImportService> log)
{
    public async Task<ImportResult> ImportAsync(string json5, ImportMode mode, CancellationToken ct = default)
    {
        var hash = Sha256(json5);

        if (mode == ImportMode.FirstBootOnly && await db.Rules.AnyAsync(ct))
            return ImportResult.Skipped("rules already exist");

        var currentHash = (await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AppSetting.ConfigImportHash, ct))?.Value;
        if (currentHash == hash)
            return ImportResult.Skipped("config unchanged");

        Dictionary<string, object?> root;
        try
        {
            root = Json5.Deserialize<Dictionary<string, object?>>(json5) ?? [];
        }
        catch (Exception ex)
        {
            log.LogError(ex, "config.json is not valid JSON5");
            return ImportResult.Skipped("invalid JSON5");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var sources = new List<SourceConnection>();
        SourceConnection? qbt = null;

        if (GetObject(root, "qbt", "qbtc", "qbt_connection") is { } qbtObj)
        {
            qbt = new SourceConnection
            {
                Name = "qbittorrent (imported)",
                Kind = SourceKind.Qbt,
                BaseUrl = Str(qbtObj, "host", "h", "url") ?? "",
                AuthMode = SourceAuthMode.UserPassword,
                Username = Str(qbtObj, "user", "u"),
            };
            SetSecret(qbt, Str(qbtObj, "pwd", "password", "p"));
            sources.Add(qbt);
        }

        if (GetObject(root, "plex", "Plex") is { } plexObj)
        {
            var plex = new SourceConnection
            {
                Name = "plex (imported)",
                Kind = SourceKind.Plex,
                BaseUrl = Str(plexObj, "url", "host") ?? "",
                AuthMode = SourceAuthMode.UserPassword,
                Username = Str(plexObj, "user", "u"),
                OptionsJson = JsonSerializer.Serialize(new
                {
                    clientId = Str(plexObj, "client_id", "clientid", "client-id"),
                }),
            };
            SetSecret(plex, Str(plexObj, "pwd", "password", "p"));
            sources.Add(plex);
        }

        db.SourceConnections.AddRange(sources);

        // Imported rules land in the single global list, each disabled, so the engine evaluates
        // nothing until the user reviews and enables them.
        var order = 0;
        var ruleCount = 0;
        foreach (var raw in GetArray(root, "AutoTorrentRules"))
        {
            if (raw is not IDictionary<string, object?> r) continue;
            var built = BuildRule(r, order);
            if (built is null) continue;

            built.Enabled = false;
            db.Rules.Add(built);
            order++;
            ruleCount++;
        }

        await UpsertSettingAsync(AppSetting.ConfigImportHash, hash, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("Imported config.json: {Sources} source(s), {Rules} rule(s)", sources.Count, ruleCount);
        return new ImportResult(true, "imported", sources.Count, ruleCount);
    }

    private Rule? BuildRule(IDictionary<string, object?> r, int order)
    {
        var type = (Str(r, "Type") ?? "").Trim();
        var name = Str(r, "Name") ?? $"Rule {order + 1}";
        var criteria = Str(r, "Criteria") ?? "true";

        var (actionType, paramsObj) = type.ToUpperInvariant() switch
        {
            "AUTOTAG" => ("tag.sync", (object)new { tag = Str(r, "Tag") ?? "" }),
            "AUTOCATEGORY" => ("category.set", new { category = Str(r, "Category") ?? "" }),
            "AUTOMOVE" => ("torrent.move", new { path = Str(r, "Path") ?? "" }),
            "AUTOSCRIPT" => ("script.run", new
            {
                runDir = Str(r, "RunDir") ?? "",
                shebang = Str(r, "Shebang") ?? "",
                script = Str(r, "Script") ?? "",
                timeout = Num(r, "Timeout") ?? 500,
            }),
            "AUTOSPEED" => ("speed.limit", new
            {
                uploadKb = Num(r, "UploadSpeed", "UploadSpeed_Kb") ?? -1,
                downloadKb = Num(r, "DownloadSpeed", "UownloadSpeed") ?? -1,   // legacy typo tolerated
            }),
            _ => (null!, null!),
        };

        if (actionType is null)
        {
            log.LogWarning("Skipping rule '{Name}' with unknown Type '{Type}'", name, type);
            return null;
        }

        return new Rule
        {
            Name = name,
            Order = order,
            Enabled = true,
            ConditionMode = RuleConditionMode.Raw,
            RawExpression = criteria,
            CompiledExpression = criteria,
            CompileValid = true,
            Action = new RuleAction
            {
                Type = actionType,
                Order = 0,
                ParamsJson = JsonSerializer.Serialize(paramsObj),
            },
        };
    }

    private void SetSecret(SourceConnection conn, string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return;
        var (ct, nonce) = secrets.Protect(plaintext);
        conn.SecretCiphertext = ct;
        conn.SecretNonce = nonce;
    }

    private async Task UpsertSettingAsync(string key, string value, CancellationToken ct)
    {
        var existing = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else existing.Value = value;
    }

    // ---- JSON5 dict helpers (values are boxed string/long/double/bool; objects are dictionaries) ----

    private static IDictionary<string, object?>? GetObject(IDictionary<string, object?> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && v is IDictionary<string, object?> obj)
                return obj;
        return null;
    }

    private static IEnumerable<object?> GetArray(IDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is IEnumerable<object?> list ? list : [];

    private static string? Str(IDictionary<string, object?> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && v is not null)
                return v.ToString();
        return null;
    }

    private static long? Num(IDictionary<string, object?> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && v is not null && long.TryParse(v.ToString(), out var n))
                return n;
        return null;
    }

    private static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
