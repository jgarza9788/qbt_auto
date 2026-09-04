using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbitFlow.Engine.Actions;
using QbitFlow.Infrastructure.Data;
using QbitFlow.Web.Api;
using QbitFlow.Web.Pages;

namespace QbitFlow.Tests.Web;

using QbitFlow.Tests;

public class RulesPageTests(QbitFlowFactory factory) : IClassFixture<QbitFlowFactory>
{
    private sealed class NoopTempData : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private static RulesModel NewPage(IServiceProvider sp) => new(
        sp.GetRequiredService<AppDbContext>(),
        sp.GetRequiredService<ActionRegistry>(),
        sp.GetRequiredService<RuleWriter>())
    {
        PageContext = new PageContext(),
        TempData = new TempDataDictionary(new DefaultHttpContext(), new NoopTempData()),
    };

    private static string RawRule(string name, string expr, int order) =>
        $$"""
        {"id":null,"name":"{{name}}","order":{{order}},"enabled":true,"stopOnMatch":null,
         "mode":"Raw","rawExpression":"{{expr}}","group":null,
         "actionType":"tag.sync","actionParamsJson":"{}","targetIds":null,"cooldownSeconds":null}
        """;

    [Fact]
    public async Task Post_saves_new_rules_in_order()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var page = NewPage(scope.ServiceProvider);
            page.RulesPayload = $"[{RawRule("r1", "true", 0)},{RawRule("r2", "<Ratio> > 1", 1)}]";
            (await page.OnPostAsync(default)).Should().BeOfType<RedirectToPageResult>();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var rules = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Rules.AsNoTracking().OrderBy(r => r.Order).ToListAsync();
            rules.Select(r => (r.Name, r.Order)).Should().Equal(("r1", 0), ("r2", 1));
        }
    }

    [Fact]
    public async Task Post_omitting_an_existing_rule_deletes_it()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await scope.ServiceProvider.GetRequiredService<RuleWriter>().ReconcileAsync(
                [new RuleWriter.RuleDraft(null, "doomed", 0, true, null, "Raw", "true", null, "tag.sync", "{}")], default);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var page = NewPage(scope.ServiceProvider);
            page.RulesPayload = "[]";
            (await page.OnPostAsync(default)).Should().BeOfType<RedirectToPageResult>();
        }

        using (var scope = factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Rules.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Post_with_malformed_payload_re_renders_and_saves_nothing()
    {
        using var scope = factory.Services.CreateScope();
        var page = NewPage(scope.ServiceProvider);
        page.RulesPayload = "{not json";

        (await page.OnPostAsync(default)).Should().BeOfType<PageResult>();
        page.ModelState.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Rules_page_renders_editor_and_field_slide_over()
    {
        var html = await factory.CreateClient().GetStringAsync("/Rules");

        html.Should().Contain("id=\"rule-rows\"")
            .And.Contain("id=\"editor\"")
            .And.Contain("id=\"rules-payload\"")
            .And.Contain("id=\"fields-dialog\"")
            .And.Contain("id=\"open-fields\"")
            .And.Contain("&lt;watch_popularity&gt;");
    }
}
