using System.Net;
using System.Text;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Adapters;
using Qbitflow.Tests.TestHelpers;
using Xunit;

namespace Qbitflow.Tests.Sources;

public class StreamystatsAdapterTests
{
    private static SourceConnectionInfo Connection(string? extraConfigJson = null) => new()
    {
        InstanceId = 7,
        InstanceName = "streamy1",
        SourceType = SourceType.Streamystats,
        BaseUrl = "http://localhost:3000",
        ApiKey = "secret",
        ExtraConfigJson = extraConfigJson,
        TimeoutSeconds = 5,
        VerifySsl = true
    };

    [Fact]
    public async Task FetchAsync_ParsesHistory_WithTheDefaultFieldMap()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("/api/history", req.RequestUri!.AbsolutePath);
            Assert.Equal("secret", Assert.Single(req.Headers.GetValues("X-Api-Key")));

            const string json = """
            [
              {"item_name":"Foo (2020)","file_path":"/media/movies/Foo.mkv","user_name":"alice","start_time":1700000000,"percent_complete":95.0}
            ]
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var adapter = new StreamystatsAdapter(new StubInstanceHttpClientFactory(handler));

        var result = await adapter.FetchAsync(Connection());

        var record = Assert.Single(result.WatchHistory);
        Assert.Equal("Foo (2020)", record.MediaTitle);
        Assert.Equal("/media/movies/Foo.mkv", record.FilePath);
        Assert.Equal("alice", record.UserName);
        Assert.Equal(95.0, record.PercentComplete);

        // The source type is what routes the row to the streamystats table at snapshot time.
        Assert.Equal(SourceType.Streamystats, record.SourceType);
    }

    [Fact]
    public async Task FetchAsync_HonoursPerInstanceOverrides_WithoutACodeChange()
    {
        // The shipped defaults are best-effort, so a differently shaped deployment has to be
        // reachable purely through ExtraConfigJson -- same contract as Jellystat/Jellyglance.
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("/api/servers/1/statistics/history", req.RequestUri!.AbsolutePath);

            const string json = """
            {"data":[{"itemName":"Bar","userName":"bob","startTime":1700000000}]}
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var adapter = new StreamystatsAdapter(new StubInstanceHttpClientFactory(handler));
        const string extraConfig = """
        {
          "historyPath": "/api/servers/1/statistics/history",
          "resultsPath": "data",
          "fieldMap": { "title": "itemName", "user": "userName", "watchedAt": "startTime" }
        }
        """;

        var result = await adapter.FetchAsync(Connection(extraConfig));

        var record = Assert.Single(result.WatchHistory);
        Assert.Equal("Bar", record.MediaTitle);
        Assert.Equal("bob", record.UserName);
    }
}
