using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using QbitFlow.Web.Startup;

namespace QbitFlow.Tests.Web;

using QbitFlow.Tests;

public class AuthGateTests
{
    private const string Secret = "s3cr3t";

    private static WebApplicationFactory<Program> Factory(string mode) =>
        new AuthFactory(mode);

    private sealed class AuthFactory(string mode) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"qbitflow-auth-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Db", $"Data Source={_dbPath}");
            builder.UseEnvironment("Testing");
            Environment.SetEnvironmentVariable("AUTH_MODE", mode);
            Environment.SetEnvironmentVariable("AUTH_SECRET", Secret);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("AUTH_MODE", null);
            Environment.SetEnvironmentVariable("AUTH_SECRET", null);
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task Healthz_is_always_exempt()
    {
        using var f = Factory("basic");
        (await f.CreateClient().GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApiKey_mode_rejects_without_key_and_accepts_with_it()
    {
        using var f = Factory("apikey");
        var client = f.CreateClient();

        (await client.GetAsync("/api/pipelines")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Add("X-Api-Key", Secret);
        (await client.GetAsync("/api/pipelines")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Basic_mode_challenges_then_accepts_the_secret_as_the_password()
    {
        using var f = Factory("basic");
        var client = f.CreateClient();

        var challenge = await client.GetAsync("/Pipelines");
        challenge.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.ToString().Should().Contain("Basic");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{Secret}")));
        (await client.GetAsync("/Pipelines")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Hash_is_stable_and_hex()
    {
        AuthGate.Hash("abc").Should().Be(AuthGate.Hash("abc")).And.MatchRegex("^[0-9A-F]{64}$");
    }
}
