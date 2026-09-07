using Npgsql;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Adapters;
using Xunit;

namespace Qbitflow.Tests.Sources;

/// <summary>
/// Streamystats is read from PostgreSQL rather than over HTTP, so what's testable without a live
/// database is how an instance's settings turn into a connection. Everything here is about that
/// translation and the guarantees around it (read-only, credentials not taken from the URL).
/// </summary>
public class StreamystatsAdapterTests
{
    private static SourceConnectionInfo Connection(
        string baseUrl = "vectorchord:5432",
        string? username = "postgres",
        string? password = "postgres",
        string? extraConfigJson = null) => new()
        {
            InstanceId = 7,
            InstanceName = "streamy1",
            SourceType = SourceType.Streamystats,
            BaseUrl = baseUrl,
            Username = username,
            Password = password,
            ExtraConfigJson = extraConfigJson,
            TimeoutSeconds = 30,
            VerifySsl = true
        };

    private static NpgsqlConnectionStringBuilder Parse(SourceConnectionInfo c) =>
        new(StreamystatsConnectionConfig.Parse(c).ConnectionString);

    [Theory]
    [InlineData("vectorchord", "vectorchord", 5432, "streamystats")]
    [InlineData("vectorchord:5433", "vectorchord", 5433, "streamystats")]
    [InlineData("192.168.1.10:5432", "192.168.1.10", 5432, "streamystats")]
    [InlineData("postgresql://vectorchord:5432/streamystats", "vectorchord", 5432, "streamystats")]
    [InlineData("postgres://db.local:6000/other_db", "db.local", 6000, "other_db")]
    public void Parse_AcceptsTheHostFormsPeopleActuallyHave(string baseUrl, string host, int port, string database)
    {
        var builder = Parse(Connection(baseUrl));

        Assert.Equal(host, builder.Host);
        Assert.Equal(port, builder.Port);
        Assert.Equal(database, builder.Database);
    }

    [Fact]
    public void Parse_TakesCredentialsFromTheEncryptedFields_NotTheUrl()
    {
        // A pasted DATABASE_URL carries a password, but BaseUrl is stored in the clear -- so the
        // URL's credentials must be ignored in favour of the encrypted Username/Password.
        var config = StreamystatsConnectionConfig.Parse(
            Connection("postgresql://leaked_user:leaked_pass@vectorchord:5432/streamystats",
                       username: "real_user", password: "real_pass"));
        var builder = new NpgsqlConnectionStringBuilder(config.ConnectionString);

        Assert.Equal("real_user", builder.Username);
        Assert.Equal("real_pass", builder.Password);
        Assert.True(config.BaseUrlHadCredentials, "the caller needs to know so it can say they were ignored");
    }

    [Fact]
    public void Parse_OpensTheConnectionReadOnly()
    {
        // The adapter only ever SELECTs; this makes PostgreSQL enforce that rather than trusting it.
        Assert.Contains("default_transaction_read_only=on", Parse(Connection()).Options);
    }

    [Fact]
    public void Parse_DefaultsToPreferSsl_SoAPlainContainerPostgresConnects()
    {
        Assert.Equal(SslMode.Prefer, Parse(Connection()).SslMode);
    }

    [Fact]
    public void Parse_AllowsOptingIntoCertificateValidation()
    {
        var builder = Parse(Connection(extraConfigJson: """{"sslMode": "VerifyFull"}"""));

        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
    }

    [Fact]
    public void Parse_ReadsDatabaseServerIdAndRowCapFromExtraConfig()
    {
        var config = StreamystatsConnectionConfig.Parse(Connection(
            extraConfigJson: """{"database": "streamy_prod", "serverId": 2, "maxRows": 500}"""));

        Assert.Equal("streamy_prod", new NpgsqlConnectionStringBuilder(config.ConnectionString).Database);
        Assert.Equal(2, config.ServerId);
        Assert.Equal(500, config.MaxRows);
    }

    [Fact]
    public void Parse_WithoutAServerId_ReadsEveryServer()
    {
        Assert.Null(StreamystatsConnectionConfig.Parse(Connection()).ServerId);
    }

    [Fact]
    public void Parse_AppliesTheInstanceTimeout()
    {
        var builder = Parse(new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = "s",
            SourceType = SourceType.Streamystats,
            BaseUrl = "host",
            Username = "u",
            Password = "p",
            TimeoutSeconds = 12
        });

        Assert.Equal(12, builder.Timeout);
        Assert.Equal(12, builder.CommandTimeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vectorchord:not-a-port")]
    [InlineData("vectorchord:70000")]
    [InlineData(":5432")]
    public void Parse_RejectsAnUnusableBaseUrl(string baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => StreamystatsConnectionConfig.Parse(Connection(baseUrl)));
    }

    [Fact]
    public void Adapter_DeclaresItsSourceType_SoRowsLandInTheStreamystatsTable()
    {
        Assert.Equal(SourceType.Streamystats, new StreamystatsAdapter().SourceType);
    }
}
