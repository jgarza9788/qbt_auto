using System.Net;
using System.Text;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Adapters;
using Qbitflow.Tests.TestHelpers;
using Xunit;

namespace Qbitflow.Tests.Sources;

public class QbtAdapterTests
{
    [Fact]
    public async Task FetchAsync_ParsesTorrents_WithoutLogin_WhenNoUsernameConfigured()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("/api/v2/torrents/info", req.RequestUri!.AbsolutePath);
            const string json = """
            [
              {"hash":"abc123","name":"Ubuntu ISO","category":"linux","tags":"iso, verified","save_path":"/downloads","content_path":"/downloads/ubuntu.iso","size":123456,"progress":1.0,"state":"uploading","downloaded":123456,"uploaded":50000,"ratio":0.4,"added_on":1700000000,"completion_on":1700005000,"up_limit":0,"dl_limit":0}
            ]
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        var connection = new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = "Main",
            SourceType = SourceType.Qbittorrent,
            BaseUrl = "http://localhost:8080",
            TimeoutSeconds = 5,
            VerifySsl = true
        };

        var result = await adapter.FetchAsync(connection);

        var torrent = Assert.Single(result.Torrents);
        Assert.Equal("abc123", torrent.Hash);
        Assert.Equal("Ubuntu ISO", torrent.Name);
        Assert.Equal("linux", torrent.Category);
        Assert.Equal(["iso", "verified"], torrent.Tags);
        Assert.Equal(123456, torrent.SizeBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), torrent.AddedOn);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700005000), torrent.CompletionOn);
    }

    [Fact]
    public async Task FetchAsync_LogsInFirst_WhenUsernameConfigured()
    {
        var loginCalled = false;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                loginCalled = true;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                response.Headers.Add("Set-Cookie", "SID=testsid; path=/");
                return response;
            }

            Assert.True(loginCalled, "the info endpoint should only be called after login");
            Assert.True(req.Headers.TryGetValues("Cookie", out var cookies) && cookies.Contains("SID=testsid"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        var connection = new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = "Main",
            SourceType = SourceType.Qbittorrent,
            BaseUrl = "http://localhost:8080",
            Username = "admin",
            Password = "pw",
            TimeoutSeconds = 5,
            VerifySsl = true
        };

        var result = await adapter.FetchAsync(connection);

        Assert.True(loginCalled);
        Assert.Empty(result.Torrents);
    }

    [Fact]
    public async Task FetchAsync_Throws_WhenLoginRejected()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Fails.") });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        var connection = new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = "Main",
            SourceType = SourceType.Qbittorrent,
            BaseUrl = "http://localhost:8080",
            Username = "admin",
            Password = "wrong",
            TimeoutSeconds = 5,
            VerifySsl = true
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.FetchAsync(connection));
        Assert.Contains("Fails.", ex.Message);
        Assert.Contains("Tools", ex.Message); // the "check its Tools -> Log" ban hint
    }

    [Fact]
    public async Task LoginAsync_Throws_WithHttpStatus_WhenAuthEndpointReturnsForbidden()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("/api/v2/auth/login", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Ban expired in 3540 seconds") };
        });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        var connection = new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = "Main",
            SourceType = SourceType.Qbittorrent,
            BaseUrl = "http://localhost:8080",
            Username = "admin",
            Password = "pw",
            TimeoutSeconds = 5,
            VerifySsl = true
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.FetchAsync(connection));
        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("Ban expired", ex.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsFailureWithStatus_WhenAuthForbidden()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("forbidden") });

        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        var connection = new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = "Main",
            SourceType = SourceType.Qbittorrent,
            BaseUrl = "http://localhost:8080",
            Username = "admin",
            Password = "pw",
            TimeoutSeconds = 5,
            VerifySsl = true
        };

        var result = await adapter.TestConnectionAsync(connection);

        Assert.False(result.Success);
        Assert.Contains("HTTP 403", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsFailure_WhenServerUnreachable()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        var adapter = new QbtAdapter(new StubInstanceHttpClientFactory(handler));
        var connection = new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = "Dead",
            SourceType = SourceType.Qbittorrent,
            BaseUrl = "http://localhost:9999",
            TimeoutSeconds = 5,
            VerifySsl = true
        };

        var result = await adapter.TestConnectionAsync(connection);

        Assert.False(result.Success);
        Assert.Contains("Connection refused", result.Message);
    }
}
