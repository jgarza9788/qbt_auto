using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Console;
using Qbitflow.Core.Interfaces;
using Qbitflow.Engine;
using Qbitflow.Engine.Actions;
using Qbitflow.Engine.Conditions;
using Qbitflow.Engine.Conditions.AdvancedSql;
using Qbitflow.Engine.Scheduling;
using Qbitflow.Infrastructure.Auth;
using Qbitflow.Infrastructure.Config;
using Qbitflow.Infrastructure.Persistence;
using Qbitflow.Infrastructure.Security;
using Qbitflow.Infrastructure.Settings;
using Qbitflow.Sources;

var builder = WebApplication.CreateBuilder(args);

// All persistent state (SQLite DB + data-protection key ring) lives under one
// configurable directory so a single volume mount is enough in Docker.
var dataDir = Environment.GetEnvironmentVariable("QBITFLOW_DATA_DIR")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);

var dbPath = Path.Combine(dataDir, "qbitflow.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Structured (JSON) logging to stdout/stderr, so `docker logs` yields parseable lines.
// The minimum level is whatever was last saved on the Settings page; since that's a DB
// value (not an IConfiguration source), it can't hot-reload -- it takes effect on the
// next restart, which is read here directly (before the DI container / DbContext exist).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = ConsoleFormatterNames.Json);
builder.Logging.SetMinimumLevel(ReadPersistedLogLevel(dbPath));

builder.Services.AddDataProtection()
    .SetApplicationName("qbitflow")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddScoped<IConfigPortabilityService, ConfigPortabilityService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddSingleton<ISetupStateAccessor, SetupStateAccessor>();
builder.Services.AddScoped<IParallelismSettingsProvider, ParallelismSettingsProvider>();
builder.Services.AddQbitflowSources();

builder.Services.AddSingleton<ConditionSqlCompiler>();
builder.Services.AddSingleton<AdvancedSqlExecutor>();
builder.Services.AddSingleton<IActionExecutor, ActionExecutor>();
builder.Services.AddScoped<IRuleRunner, RuleRunner>();
builder.Services.AddSingleton<RuleRunGate>();
builder.Services.AddHostedService<RuleSchedulerService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Cookie.Name = "qbitflow.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Setup");
    options.Conventions.AllowAnonymousToPage("/Error");
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();

// First-run gate: until the setup wizard has created the admin account, every
// request (other than the wizard itself, health checks, and static assets) is
// redirected to /Setup -- this runs before authentication so it works even
// though no user/cookie exists yet.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/Setup") || path.StartsWithSegments("/healthz")
        || path.Value?.Contains('.') == true)
    {
        await next();
        return;
    }

    var setupState = context.RequestServices.GetRequiredService<ISetupStateAccessor>();
    var db = context.RequestServices.GetRequiredService<AppDbContext>();
    if (!await setupState.IsSetupCompleteAsync(db))
    {
        context.Response.Redirect("/Setup");
        return;
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", async (AppDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "unhealthy", reason = "database unreachable" }, statusCode: 503);
});

app.MapPost("/Logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/Login");
}).RequireAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

static LogLevel ReadPersistedLogLevel(string dbPath)
{
    if (!File.Exists(dbPath))
    {
        return LogLevel.Information;
    }

    try
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LogLevel FROM AppSettings WHERE Id = 1";
        return (command.ExecuteScalar() as string) switch
        {
            "Trace" => LogLevel.Trace,
            "Debug" => LogLevel.Debug,
            "Information" => LogLevel.Information,
            "Warning" => LogLevel.Warning,
            "Error" => LogLevel.Error,
            "Critical" => LogLevel.Critical,
            _ => LogLevel.Information
        };
    }
    catch
    {
        // First run before migrations, or an unreadable file -- fall back to a sane default.
        return LogLevel.Information;
    }
}
