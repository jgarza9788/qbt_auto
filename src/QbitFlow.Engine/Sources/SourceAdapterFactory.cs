using System.Collections.Concurrent;
using System.Text.Json;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Config;
using QbitFlow.Sources.Jellyfin;
using QbitFlow.Sources.Plex;
using QbitFlow.Sources.Qbt;

namespace QbitFlow.Engine.Sources;

/// <summary>
/// One factory for every source kind. Resolves connection details (with env-var overrides + secret
/// decryption) via <see cref="SourceConnectionReader"/>, then builds and caches the right adapter:
/// <see cref="QbtGateway"/> for qBittorrent, <see cref="PlexAdapter"/> / <see cref="JellyfinAdapter"/>
/// for media libraries. Also serves the <see cref="IQbtGatewayFactory"/> the pipeline runner uses.
/// </summary>
public sealed class SourceAdapterFactory(
    SourceConnectionReader reader,
    IHttpClientFactory httpClientFactory)
    : ISourceAdapterFactory, IQbtGatewayFactory, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, QbtGateway> _qbt = new();
    private readonly ConcurrentDictionary<Guid, IMediaSourceAdapter> _media = new();

    public IQbtAdapter GetQbtAdapter(Guid id) => Qbt(id);
    public IQbtActionTarget GetQbtActionTarget(Guid id) => Qbt(id);
    public IQbtAdapter GetAdapter(Guid id) => Qbt(id);              // IQbtGatewayFactory
    public IQbtActionTarget GetActionTarget(Guid id) => Qbt(id);   // IQbtGatewayFactory

    public IMediaSourceAdapter GetMediaAdapter(Guid id) =>
        _media.GetOrAdd(id, BuildMedia);

    public void Invalidate(Guid id)
    {
        if (_qbt.TryRemove(id, out var gw)) _ = gw.DisposeAsync().AsTask();
        _media.TryRemove(id, out _);
    }

    private QbtGateway Qbt(Guid id) => _qbt.GetOrAdd(id, BuildQbt);

    private QbtGateway BuildQbt(Guid id)
    {
        var c = Resolve(id);
        if (c.Kind != SourceKind.Qbt)
            throw new InvalidOperationException($"Source {id} is {c.Kind}, not qBittorrent.");

        var (verifyTls, timeout) = QbtOptions(c.OptionsJson);
        return new QbtGateway(new QbtConnectionInfo(
            c.Id, new Uri(c.BaseUrl), c.Username, c.Secret, verifyTls, timeout));
    }

    private IMediaSourceAdapter BuildMedia(Guid id)
    {
        var c = Resolve(id);
        return c.Kind switch
        {
            SourceKind.Plex => new PlexAdapter(
                httpClientFactory.CreateClient("plex"),
                new PlexConnectionInfo(c.Id, new Uri(c.BaseUrl), c.AuthMode, c.Username, c.Secret, PlexClientId(c.OptionsJson))),

            SourceKind.Jellyfin => new JellyfinAdapter(
                httpClientFactory.CreateClient("jellyfin"),
                new JellyfinConnectionInfo(c.Id, new Uri(c.BaseUrl), c.Secret, JellyfinUserScope(c.OptionsJson))),

            _ => throw new InvalidOperationException($"Source {id} is {c.Kind}, not a media library."),
        };
    }

    private ResolvedConnection Resolve(Guid id) => reader.ResolveAsync(id).GetAwaiter().GetResult();

    // ---- options parsing ----

    private static (bool VerifyTls, int TimeoutSec) QbtOptions(string json)
    {
        var root = Root(json);
        var verify = !root.TryGetProperty("verifyTls", out var v) || v.ValueKind != JsonValueKind.False;
        var timeout = root.TryGetProperty("httpTimeoutSec", out var t) && t.TryGetInt32(out var s) ? s : 30;
        return (verify, timeout);
    }

    private static string PlexClientId(string json) =>
        Root(json).TryGetProperty("clientId", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "qbit-flow"
            : "qbit-flow";

    private static string JellyfinUserScope(string json) =>
        Root(json).TryGetProperty("userScope", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "all"
            : "all";

    private static JsonElement Root(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gw in _qbt.Values) await gw.DisposeAsync();
        _qbt.Clear();
        _media.Clear();
    }
}
