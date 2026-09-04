using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Sources;
using QbitFlow.Infrastructure.Config;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Api;

internal static class SourcesApi
{
    public sealed record SourceInput(
        string Name, string Kind, string BaseUrl, bool Enabled,
        string? AuthMode, string? Username, string? Secret, string? OptionsJson);

    public sealed record ConfigImportRequest(string? Content, string? Path, bool Force = false);

    public static void MapSourcesApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/sources");

        api.MapGet("/", async (AppDbContext db) => Results.Json(
            (await db.SourceConnections.AsNoTracking().OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync())
                .Select(ToDto)));

        api.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var s = await db.SourceConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return s is null ? Results.NotFound() : Results.Json(ToDto(s));
        });

        api.MapPost("/", async (SourceInput input, AppDbContext db, ISecretProtector secrets) =>
        {
            var s = new SourceConnection();
            Apply(s, input, secrets, isNew: true);
            db.SourceConnections.Add(s);
            await db.SaveChangesAsync();
            return Results.Created($"/api/sources/{s.Id}", ToDto(s));
        });

        api.MapPut("/{id:guid}", async (Guid id, SourceInput input, AppDbContext db, ISecretProtector secrets, SourceCacheInvalidator caches) =>
        {
            var s = await db.SourceConnections.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            Apply(s, input, secrets, isNew: false);
            s.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            caches.Invalidate(id);   // the edit may have changed the URL or credentials
            return Results.Json(ToDto(s));
        });

        api.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, SourceCacheInvalidator caches) =>
        {
            var s = await db.SourceConnections.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            db.SourceConnections.Remove(s);
            await db.SaveChangesAsync();
            caches.Invalidate(id);
            return Results.NoContent();
        });

        api.MapPost("/{id:guid}/test", async (Guid id, AppDbContext db, SourceAdapterFactory adapters, SourceCacheInvalidator caches, CancellationToken ct) =>
        {
            var s = await db.SourceConnections.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();

            caches.Invalidate(id);   // pick up any just-saved edit
            HealthResult result;
            try
            {
                result = s.Kind == SourceKind.Qbt
                    ? await adapters.GetQbtAdapter(id).TestAsync(ct)
                    : await adapters.GetMediaAdapter(id).TestAsync(ct);
            }
            catch (Exception ex)
            {
                result = HealthResult.Unhealthy(ex.Message);
            }

            s.HealthState = result.Ok ? HealthState.Healthy : HealthState.Unreachable;
            s.LastCheckedUtc = DateTimeOffset.UtcNow;
            s.LatencyMs = result.LatencyMs;
            s.LastError = result.Ok ? null : result.Error;
            await db.SaveChangesAsync(ct);

            return Results.Json(new { ok = result.Ok, result.LatencyMs, error = result.Error });
        });

        api.MapGet("/{id:guid}/sample", async (Guid id, AppDbContext db, SourceAdapterFactory adapters, CancellationToken ct) =>
        {
            var s = await db.SourceConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (s.Kind == SourceKind.Qbt) return Results.BadRequest(new { error = "sample is for media sources" });

            var adapter = adapters.GetMediaAdapter(id);
            var media = await adapter.FetchMediaAsync(ct);
            var watch = await adapter.FetchWatchAsync(DateTimeOffset.UtcNow.AddYears(-1), ct);

            return Results.Json(new
            {
                mediaCount = media.Count,
                media = media.Take(10).Select(m => new { m.Title, m.MediaType, m.Year, m.Rating, files = m.Files.Count }),
                watchCount = watch.Count,
                watch = watch.OrderByDescending(w => w.PlayCount).Take(10)
                    .Select(w => new { w.Title, w.MediaType, w.PlayCount, w.LastPlayedUtc }),
            });
        });

        app.MapPost("/api/config/import", async (ConfigImportRequest req, ConfigImportService importer, CancellationToken ct) =>
        {
            string json5;
            if (!string.IsNullOrWhiteSpace(req.Content)) json5 = req.Content!;
            else if (!string.IsNullOrWhiteSpace(req.Path) && File.Exists(req.Path)) json5 = await File.ReadAllTextAsync(req.Path!, ct);
            else return Results.BadRequest(new { error = "provide 'content' or a readable 'path'" });

            var result = await importer.ImportAsync(json5, req.Force ? ImportMode.Force : ImportMode.FirstBootOnly, ct);
            return Results.Json(result);
        });
    }

    private static void Apply(SourceConnection s, SourceInput input, ISecretProtector secrets, bool isNew)
    {
        s.Name = input.Name.Trim();
        s.Kind = Enum.Parse<SourceKind>(input.Kind, ignoreCase: true);
        s.BaseUrl = input.BaseUrl.Trim();
        s.Enabled = input.Enabled;
        s.AuthMode = Enum.TryParse<SourceAuthMode>(input.AuthMode, true, out var am) ? am : SourceAuthMode.UserPassword;
        s.Username = input.Username;
        s.OptionsJson = string.IsNullOrWhiteSpace(input.OptionsJson) ? "{}" : input.OptionsJson!;

        if (!string.IsNullOrEmpty(input.Secret))
        {
            var (cipher, nonce) = secrets.Protect(input.Secret!);
            s.SecretCiphertext = cipher;
            s.SecretNonce = nonce;
        }
        else if (isNew)
        {
            s.SecretCiphertext = null;
            s.SecretNonce = null;
        }
        // on update with no secret supplied → keep the existing one
    }

    private static object ToDto(SourceConnection s) => new
    {
        s.Id, s.Name, kind = s.Kind.ToString(), s.BaseUrl, s.Enabled, s.Username,
        authMode = s.AuthMode.ToString(), hasSecret = s.SecretCiphertext is not null,
        health = s.HealthState.ToString(), s.LastCheckedUtc, s.LatencyMs, s.LastError,
        s.OptionsJson,
    };
}
