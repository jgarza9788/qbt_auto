using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace QbitFlow.Tests;

public sealed class QbitFlowFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"qbitflow-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Db", $"Data Source={_dbPath}");
        builder.UseEnvironment("Testing");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch { /* temp file cleanup is best effort */ }
    }
}

public class HealthEndpointTests(QbitFlowFactory factory) : IClassFixture<QbitFlowFactory>
{
    [Fact]
    public async Task Healthz_returns_200_and_migrates_the_database()
    {
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/healthz");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("healthy");
    }

    [Fact]
    public async Task Health_reports_ready_when_the_database_is_reachable()
    {
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
