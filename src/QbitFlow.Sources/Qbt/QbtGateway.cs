using System.Diagnostics;
using System.Net;
using QBittorrent.Client;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;

namespace QbitFlow.Sources.Qbt;

/// <summary>
/// A single qBittorrent instance — both the data source (<see cref="IQbtAdapter"/>) and the action
/// target (<see cref="IQbtActionTarget"/>). Wraps <c>QBittorrent.Client</c>; logs in lazily and
/// re-logs in on an auth failure.
/// </summary>
public sealed class QbtGateway : IQbtAdapter, IQbtActionTarget, IAsyncDisposable
{
    private readonly QbtConnectionInfo _info;
    private readonly QBittorrentClient _client;
    private readonly HttpClient _raw;   // for WebUI endpoints the client library doesn't wrap (export)
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private volatile bool _loggedIn;
    private volatile bool _rawLoggedIn;

    public QbtGateway(QbtConnectionInfo info)
    {
        _info = info;

        SocketsHttpHandler NewHandler() => new()
        {
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
        };

        var handler = NewHandler();
        var rawHandler = NewHandler();
        rawHandler.UseCookies = true;
        rawHandler.CookieContainer = new System.Net.CookieContainer();
        if (!info.VerifyTls)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            rawHandler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        _client = new QBittorrentClient(info.BaseUrl, handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(info.HttpTimeoutSeconds),
        };
        _raw = new HttpClient(rawHandler, disposeHandler: true)
        {
            BaseAddress = info.BaseUrl,
            Timeout = TimeSpan.FromSeconds(info.HttpTimeoutSeconds),
        };
        _raw.DefaultRequestHeaders.Referrer = info.BaseUrl;
    }

    public Guid SourceId => _info.SourceId;

    public async Task<HealthResult> TestAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureLoggedInAsync(force: true, ct);
            _ = await _client.GetApiVersionAsync(ct);
            return HealthResult.Healthy((int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return HealthResult.Unhealthy(ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<TorrentView>> FetchTorrentsAsync(CancellationToken ct)
    {
        var torrents = await WithAuth(() => _client.GetTorrentListAsync(token: ct));
        return torrents.Select(TorrentMapper.ToView).ToArray();
    }

    public Task AddTagAsync(string hash, string tag, CancellationToken ct) =>
        WithAuth(() => _client.AddTorrentTagAsync(hash, tag, ct));

    public Task RemoveTagAsync(string hash, string tag, CancellationToken ct) =>
        WithAuth(() => _client.DeleteTorrentTagAsync(hash, tag, ct));

    public async Task SetCategoryAsync(string hash, string category, bool enableAutoManagement, CancellationToken ct)
    {
        await WithAuth(() => _client.SetAutomaticTorrentManagementAsync(hash, enableAutoManagement, ct));
        await WithAuth(() => _client.SetTorrentCategoryAsync(hash, category, ct));
    }

    public async Task SetLocationAsync(string hash, string path, bool disableAutoManagement, CancellationToken ct)
    {
        if (disableAutoManagement)
            await WithAuth(() => _client.SetAutomaticTorrentManagementAsync(hash, false, ct));
        await WithAuth(() => _client.SetLocationAsync(hash, path, ct));
    }

    public Task SetUploadLimitAsync(string hash, long bytesPerSecond, CancellationToken ct) =>
        WithAuth(() => _client.SetTorrentUploadLimitAsync(hash, Math.Max(0, bytesPerSecond), ct));

    public Task SetDownloadLimitAsync(string hash, long bytesPerSecond, CancellationToken ct) =>
        WithAuth(() => _client.SetTorrentDownloadLimitAsync(hash, Math.Max(0, bytesPerSecond), ct));

    public Task PauseAsync(string hash, CancellationToken ct) =>
        WithAuth(() => _client.PauseAsync(hash, ct));

    public Task ResumeAsync(string hash, CancellationToken ct) =>
        WithAuth(() => _client.ResumeAsync(hash, ct));

    public Task SetForceStartAsync(string hash, bool on, CancellationToken ct) =>
        WithAuth(() => _client.SetForceStartAsync(hash, on, ct));

    public async Task<Stream> ExportTorrentAsync(string hash, CancellationToken ct)
    {
        await EnsureRawLoginAsync(force: false, ct);
        var res = await _raw.GetAsync($"api/v2/torrents/export?hash={hash}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.Forbidden)
        {
            await EnsureRawLoginAsync(force: true, ct);
            res = await _raw.GetAsync($"api/v2/torrents/export?hash={hash}", HttpCompletionOption.ResponseHeadersRead, ct);
        }
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStreamAsync(ct);
    }

    // ---- auth plumbing ----

    private async Task EnsureRawLoginAsync(bool force, CancellationToken ct)
    {
        if (_rawLoggedIn && !force) return;
        await _loginGate.WaitAsync(ct);
        try
        {
            if (_rawLoggedIn && !force) return;
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", _info.Username),
                new KeyValuePair<string, string>("password", _info.Password),
            });
            var res = await _raw.PostAsync("api/v2/auth/login", form, ct);
            res.EnsureSuccessStatusCode();
            _rawLoggedIn = true;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task EnsureLoggedInAsync(bool force, CancellationToken ct)
    {
        if (_loggedIn && !force) return;
        await _loginGate.WaitAsync(ct);
        try
        {
            if (_loggedIn && !force) return;
            await _client.LoginAsync(_info.Username, _info.Password, ct);
            _loggedIn = true;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task WithAuth(Func<Task> action)
    {
        await EnsureLoggedInAsync(force: false, CancellationToken.None);
        try
        {
            await action();
        }
        catch (QBittorrentClientRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            await EnsureLoggedInAsync(force: true, CancellationToken.None);
            await action();
        }
    }

    private async Task<T> WithAuth<T>(Func<Task<T>> action)
    {
        await EnsureLoggedInAsync(force: false, CancellationToken.None);
        try
        {
            return await action();
        }
        catch (QBittorrentClientRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            await EnsureLoggedInAsync(force: true, CancellationToken.None);
            return await action();
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _raw.Dispose();
        _loginGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
