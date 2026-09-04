using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Config;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Tests.Infrastructure;

public class ConfigImportTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    private const string Config = """
        {
          // legacy-style config with comments and trailing commas
          "qbt": { "host": "http://192.168.1.10:8080", "user": "admin", "pwd": "secret" },
          "plex": { "url": "http://192.168.1.10:32400", "user": "me", "pwd": "pw", "client_id": "abc" },
          "AutoTorrentRules": [
            { "Name": "Tag_SmallFile", "Type": "AutoTag", "Tag": "small_file", "Criteria": "(<Size> < 1073741824)" },
            { "Name": "Cat_Movies", "Type": "AutoCategory", "Category": "Movies", "Criteria": "match(\"<Name>\", \"1080p\")" },
            { "Name": "Speed", "Type": "AutoSpeed", "UploadSpeed": 0, "UownloadSpeed": 0, "Criteria": "true" },
            { "Name": "Bogus", "Type": "AutoWhat", "Criteria": "true" },
          ],
        }
        """;

    [Fact]
    public async Task Imports_sources_and_rules_then_is_idempotent()
    {
        await using var scope = fx.Services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<ConfigImportService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var first = await importer.ImportAsync(Config, ImportMode.Force);
        first.Imported.Should().BeTrue();
        first.Sources.Should().Be(2);
        first.Rules.Should().Be(3);   // AutoWhat skipped

        var rules = await db.Rules.Include(r => r.Action).OrderBy(r => r.Order).ToListAsync();
        rules.Should().HaveCount(3);
        rules.Should().OnlyContain(r => !r.Enabled);   // imported disabled, pending review

        var tagRule = rules.Single(r => r.Name == "Tag_SmallFile");
        tagRule.ConditionMode.Should().Be(RuleConditionMode.Raw);
        tagRule.RawExpression.Should().Be("(<Size> < 1073741824)");
        tagRule.Action!.Type.Should().Be("tag.sync");
        tagRule.Action.ParamsJson.Should().Contain("small_file");

        rules.Single(r => r.Name == "Speed").Action!.ParamsJson
            .Should().Contain("\"downloadKb\":0");   // legacy "UownloadSpeed" typo tolerated

        var qbt = await db.SourceConnections.SingleAsync(s => s.Kind == SourceKind.Qbt);
        qbt.SecretCiphertext.Should().NotBeNull();

        // second import of the same content is a no-op
        var second = await importer.ImportAsync(Config, ImportMode.Force);
        second.Imported.Should().BeFalse();
        (await db.Rules.CountAsync()).Should().Be(3);
    }
}
