using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Config;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Tests.Infrastructure;

public class SourceConnectionReaderTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    private async Task<Guid> SeedAsync(string name, SourceKind kind, string secret)
    {
        await using var scope = fx.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var (cipher, nonce) = protector.Protect(secret);

        var conn = new SourceConnection
        {
            Name = name, Kind = kind, BaseUrl = "http://db-value:8080",
            Username = "db-user", SecretCiphertext = cipher, SecretNonce = nonce,
        };
        db.SourceConnections.Add(conn);
        await db.SaveChangesAsync();
        return conn.Id;
    }

    [Fact]
    public async Task Uses_db_values_when_no_env_is_set()
    {
        var id = await SeedAsync("plain qbt", SourceKind.Qbt, "db-secret");
        await using var scope = fx.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<SourceConnectionReader>();

        var resolved = await reader.ResolveAsync(id);

        resolved.BaseUrl.Should().Be("http://db-value:8080");
        resolved.Username.Should().Be("db-user");
        resolved.Secret.Should().Be("db-secret");
    }

    [Fact]
    public async Task Generic_env_override_wins()
    {
        var id = await SeedAsync("My Qbt Box", SourceKind.Qbt, "db-secret");
        Environment.SetEnvironmentVariable("SOURCE__MY_QBT_BOX__BASEURL", "http://env:9000");
        Environment.SetEnvironmentVariable("SOURCE__MY_QBT_BOX__SECRET", "env-secret");
        try
        {
            await using var scope = fx.Services.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<SourceConnectionReader>();
            var resolved = await reader.ResolveAsync(id);

            resolved.BaseUrl.Should().Be("http://env:9000");
            resolved.Secret.Should().Be("env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOURCE__MY_QBT_BOX__BASEURL", null);
            Environment.SetEnvironmentVariable("SOURCE__MY_QBT_BOX__SECRET", null);
        }
    }

    [Fact]
    public async Task Kind_shortcut_env_override_wins()
    {
        var id = await SeedAsync("shortcut plex", SourceKind.Plex, "db-pw");
        Environment.SetEnvironmentVariable("PLEX_URL", "http://plex-env:32400");
        Environment.SetEnvironmentVariable("PLEX_PWD", "env-pw");
        try
        {
            await using var scope = fx.Services.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<SourceConnectionReader>();
            var resolved = await reader.ResolveAsync(id);

            resolved.BaseUrl.Should().Be("http://plex-env:32400");
            resolved.Secret.Should().Be("env-pw");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLEX_URL", null);
            Environment.SetEnvironmentVariable("PLEX_PWD", null);
        }
    }
}
