using Microsoft.EntityFrameworkCore;
using QbitFlow.Engine;
using QbitFlow.Engine.RuleEngine;
using QbitFlow.Infrastructure;
using QbitFlow.Infrastructure.Data;
using QbitFlow.Web.Api;
using QbitFlow.Web.Realtime;
using QbitFlow.Web.Startup;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    var connectionString = builder.Configuration.GetConnectionString("Db")
        ?? "Data Source=data/qbitflow.db";

    builder.Services.AddQbitFlowInfrastructure(connectionString);
    builder.Services.AddQbitFlowEngine();
    builder.Services.AddSingleton<RunLogBus>();
    builder.Services.AddSingleton<QbitFlow.Core.Abstractions.IRunLogPublisher>(sp => sp.GetRequiredService<RunLogBus>());
    builder.Services.AddScoped<QbitFlow.Web.Api.RuleWriter>();

    // Background schedulers are noise in integration tests.
    if (!builder.Environment.IsEnvironment("Testing"))
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RuleEngineService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<QbitFlow.Engine.Health.SourceHealthService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<QbitFlow.Engine.Health.PathDiagnosticsService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<QbitFlow.Engine.Analytics.AnalyticsRefreshService>());
    }

    builder.Services.AddProblemDetails();
    builder.Services.AddRazorPages();

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
        Log.Information("Database migrated ({ConnectionString})", connectionString);

        await ConfigImport.RunFirstBootAsync(scope.ServiceProvider, app.Configuration, app.Lifetime.ApplicationStopping);
    }

    app.UseSerilogRequestLogging();
    app.UseStatusCodePages();
    app.UseStaticFiles();
    app.UseAuthGate();

    app.MapRazorPages();

    app.MapHealthEndpoints();
    app.MapMetaApi();
    app.MapEngineApi();
    app.MapRulesApi();
    app.MapRunsApi();
    app.MapSourcesApi();
    app.MapAnalyticsApi();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "qbit-flow failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so integration tests can host the app via <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
