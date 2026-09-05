using System.Net;
using System.Text;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Adapters;
using Qbitflow.Tests.TestHelpers;
using Xunit;

namespace Qbitflow.Tests.Sources;

public class QbtAdapterActionTests
{
    private static SourceConnectionInfo Connection() => new()
    {
        InstanceId = 1,
        InstanceName = "Main",
        SourceType = SourceType.Qbittorrent,
        BaseUrl = "http://localhost:8080",
        TimeoutSeconds = 5,
        VerifySsl = true
    };

    [Fact]
    public async Task AddTagsAsync_JoinsHashesWithPipe_AndTagsWithComma()
    {
        string? capturedBody = null;
        string? capturedPath = null;

        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        await adapter.AddTagsAsync(Connection(), ["h1", "h2"], ["done", "verified"]);

        Assert.Equal("/api/v2/torrents/addTags", capturedPath);
        Assert.Equal("hashes=h1%7Ch2&tags=done%2Cverified", capturedBody);
    }

    [Fact]
    public async Task RemoveTagsAsync_PostsExpectedForm()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        await adapter.RemoveTagsAsync(Connection(), ["h1"], ["stale"]);

        Assert.Equal("hashes=h1&tags=stale", capturedBody);
    }

    [Fact]
    public async Task SetCategoryAsync_PostsCategory_ThenEnablesAutoTmm()
    {
        var calls = new List<(string Path, string Body)>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            calls.Add((req.RequestUri!.AbsolutePath, req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        await adapter.SetCategoryAsync(Connection(), ["h1", "h2"], "archived");

        Assert.Equal(2, calls.Count);
        Assert.Equal("/api/v2/torrents/setCategory", calls[0].Path);
        Assert.Equal("hashes=h1%7Ch2&category=archived", calls[0].Body);
        Assert.Equal("/api/v2/torrents/setAutoManagement", calls[1].Path);
        Assert.Equal("hashes=h1%7Ch2&enable=true", calls[1].Body);
    }

    [Fact]
    public async Task SetLocationAsync_DisablesAutoTmm_ThenPostsLocation()
    {
        var calls = new List<(string Path, string Body)>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            calls.Add((req.RequestUri!.AbsolutePath, req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        await adapter.SetLocationAsync(Connection(), ["h1"], "/media/cold-storage");

        Assert.Equal(2, calls.Count);
        Assert.Equal("/api/v2/torrents/setAutoManagement", calls[0].Path);
        Assert.Equal("hashes=h1&enable=false", calls[0].Body);
        Assert.Equal("/api/v2/torrents/setLocation", calls[1].Path);
        Assert.Equal("hashes=h1&location=%2Fmedia%2Fcold-storage", calls[1].Body);
    }

    [Fact]
    public async Task SetUploadLimitAsync_PostsExpectedForm()
    {
        string? capturedPath = null;
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        await adapter.SetUploadLimitAsync(Connection(), ["h1"], 500_000);

        Assert.Equal("/api/v2/torrents/setUploadLimit", capturedPath);
        Assert.Equal("hashes=h1&limit=500000", capturedBody);
    }

    [Fact]
    public async Task SetDownloadLimitAsync_PostsExpectedForm()
    {
        string? capturedPath = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        await adapter.SetDownloadLimitAsync(Connection(), ["h1"], 0);

        Assert.Equal("/api/v2/torrents/setDownloadLimit", capturedPath);
    }

    [Fact]
    public async Task GetCurrentStateAsync_ParsesTagsAndCategoryAndLimits()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("/api/v2/torrents/info", req.RequestUri!.AbsolutePath);
            Assert.Equal("hashes=h1%7Ch2", req.RequestUri!.Query.TrimStart('?'));

            const string json = """
            [
              {"hash":"h1","name":"A","category":"linux","tags":"done, verified","save_path":"/downloads/a","size":1,"progress":1,"up_limit":1000,"dl_limit":2000},
              {"hash":"h2","name":"B","category":"","tags":"","save_path":"/downloads/b","size":1,"progress":1,"up_limit":0,"dl_limit":0}
            ]
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        var state = await adapter.GetCurrentStateAsync(Connection(), ["h1", "h2"]);

        Assert.Equal(2, state.Count);
        Assert.Contains("done", state["h1"].Tags);
        Assert.Contains("verified", state["h1"].Tags);
        Assert.Equal("linux", state["h1"].Category);
        Assert.Equal(1000, state["h1"].UploadLimitBytesPerSec);
        Assert.Equal(2000, state["h1"].DownloadLimitBytesPerSec);

        Assert.Null(state["h2"].Category);
        Assert.Empty(state["h2"].Tags);
    }

    [Fact]
    public async Task WriteMethods_LogInFirst_WhenCredentialsConfigured()
    {
        var loginCalled = false;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                loginCalled = true;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                response.Headers.Add("Set-Cookie", "SID=abc123; path=/");
                return response;
            }

            Assert.True(loginCalled);
            Assert.True(req.Headers.TryGetValues("Cookie", out var cookies) && cookies.Contains("SID=abc123"));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var connWithAuth = new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = "Main",
            SourceType = SourceType.Qbittorrent,
            BaseUrl = "http://localhost:8080",
            TimeoutSeconds = 5,
            VerifySsl = true,
            Username = "admin",
            Password = "pw"
        };

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        await adapter.SetCategoryAsync(connWithAuth, ["h1"], "x");

        Assert.True(loginCalled);
    }
}
