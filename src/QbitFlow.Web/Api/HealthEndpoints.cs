using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Api;

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness — process is up, no dependencies touched.
        app.MapGet("/healthz", () => Results.Text("healthy")).ExcludeFromDescription();

        // Readiness — the database is reachable.
        app.MapGet("/health", async (AppDbContext db) =>
            await db.Database.CanConnectAsync()
                ? Results.Json(new { status = "ready" })
                : Results.Json(new { status = "degraded" }, statusCode: 503))
            .ExcludeFromDescription();
    }
}
