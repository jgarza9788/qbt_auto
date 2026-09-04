using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbitFlow.Infrastructure.Data;
using QbitFlow.Web.Api;

namespace QbitFlow.Tests.Web;

using QbitFlow.Tests;

public class RuleWriterTests(QbitFlowFactory factory) : IClassFixture<QbitFlowFactory>
{
    private static RuleWriter.RuleDraft Raw(Guid? id, string name, string expr) =>
        new(id, name, 0, true, null, "Raw", expr, null, "tag.sync", "{}");

    private async Task ReconcileAsync(params RuleWriter.RuleDraft[] drafts)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await scope.ServiceProvider.GetRequiredService<RuleWriter>().ReconcileAsync(drafts, default);
        await db.SaveChangesAsync();
    }

    private async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task Adds_new_rules_in_payload_order()
    {
        await ReconcileAsync(Raw(null, "first", "true"), Raw(null, "second", "<Ratio> > 2"));

        var rules = await ReadAsync(db => db.Rules.AsNoTracking().OrderBy(r => r.Order).ToListAsync());
        rules.Select(r => (r.Name, r.Order)).Should().Equal(("first", 0), ("second", 1));
    }

    [Fact]
    public async Task Omitting_a_rule_deletes_it_and_its_condition_group()
    {
        var group = new RuleWriter.GroupDto("And", [new RuleWriter.CondDto("Category", "Eq", "String", "Movies")], null);
        await ReconcileAsync(new RuleWriter.RuleDraft(null, "builder", 0, true, null, "Builder", null, group, "tag.sync", "{}"));

        var ruleId = await ReadAsync(db => db.Rules.Where(r => r.Name == "builder").Select(r => r.Id).SingleAsync());
        (await ReadAsync(db => db.RuleConditionGroups.CountAsync(g => g.RuleId == ruleId))).Should().BeGreaterThan(0);

        await ReconcileAsync();

        (await ReadAsync(db => db.Rules.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.RuleConditionGroups.CountAsync(g => g.RuleId == ruleId))).Should().Be(0);
        (await ReadAsync(db => db.RuleConditions.CountAsync(c => c.Group!.RuleId == ruleId))).Should().Be(0);
    }

    [Fact]
    public async Task Reorders_and_updates_existing_rules_by_id()
    {
        await ReconcileAsync(Raw(null, "a", "true"), Raw(null, "b", "true"));
        var seeded = await ReadAsync(db => db.Rules.AsNoTracking().OrderBy(r => r.Order).ToListAsync());
        var (a, b) = (seeded[0].Id, seeded[1].Id);

        await ReconcileAsync(Raw(b, "b2", "true"), Raw(a, "a", "true"));

        var rules = await ReadAsync(db => db.Rules.AsNoTracking().OrderBy(r => r.Order).ToListAsync());
        rules.Select(r => (r.Id, r.Name, r.Order)).Should().Equal((b, "b2", 0), (a, "a", 1));
    }

    [Fact]
    public async Task Target_filter_and_cooldown_round_trip()
    {
        var t1 = Guid.NewGuid();
        await ReconcileAsync(Raw(null, "x", "true") with { TargetIds = [t1], CooldownSeconds = 300 });

        var rule = await ReadAsync(db => db.Rules.AsNoTracking().SingleAsync(r => r.Name == "x"));
        rule.CooldownSeconds.Should().Be(300);
        rule.TargetFilterJson.Should().Contain(t1.ToString());

        await ReconcileAsync(Raw(rule.Id, "x", "true"));   // cleared
        var cleared = await ReadAsync(db => db.Rules.AsNoTracking().SingleAsync(r => r.Name == "x"));
        cleared.CooldownSeconds.Should().BeNull();
        cleared.TargetFilterJson.Should().BeNull();
    }
}
