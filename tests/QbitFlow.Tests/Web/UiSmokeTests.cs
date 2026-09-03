using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace QbitFlow.Tests.Web;

using QbitFlow.Tests;

public class UiSmokeTests(QbitFlowFactory factory) : IClassFixture<QbitFlowFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/Pipelines")]
    [InlineData("/Sources")]
    [InlineData("/Runs")]
    [InlineData("/Analytics")]
    [InlineData("/Settings")]
    public async Task Page_renders(string path)
    {
        var resp = await factory.CreateClient().GetAsync(path);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("qbit").And.Contain("<title>");
    }

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/lib/htmx.min.js")]
    [InlineData("/lib/htmx-ext-sse.js")]
    [InlineData("/lib/sortable.min.js")]
    public async Task Static_asset_serves(string path)
    {
        var resp = await factory.CreateClient().GetAsync(path);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(500);
    }

    [Fact]
    public async Task Rule_compile_endpoint_emits_an_expression()
    {
        var client = factory.CreateClient();
        var body = new
        {
            logic = "And",
            conditions = new[]
            {
                new { field = "Size", @operator = "Lt", valueKind = "Number", value = "1073741824" },
                new { field = "Category", @operator = "Eq", valueKind = "String", value = "Movies" },
            },
            children = Array.Empty<object>(),
        };

        var resp = await client.PostAsJsonAsync("/api/rules/compile", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadFromJsonAsync<CompileResponse>();
        json!.valid.Should().BeTrue();
        json.expression.Should().Be("(<Size> < 1073741824 && \"<Category>\" == \"Movies\")");
    }

    private sealed record CompileResponse(string expression, bool valid, string[] errors);
}
