using System.Security.Cryptography;
using System.Text;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Config;

namespace QbitFlow.Web.Startup;

/// <summary>
/// Optional single-credential gate. <c>AuthMode</c> ∈ <c>none | apikey | basic</c> (env
/// <c>AUTH_MODE</c> overrides the DB setting; env <c>AUTH_SECRET</c> overrides the stored hash).
/// <c>apikey</c>: <c>X-Api-Key</c> header / <c>?apikey=</c> / <c>qf_key</c> cookie.
/// <c>basic</c>: HTTP Basic, password (or username) must equal the secret.
/// </summary>
public static class AuthGate
{
    private static readonly string[] Exempt = ["/healthz", "/health", "/app.css", "/favicon.ico"];

    public static IApplicationBuilder UseAuthGate(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            if (path.StartsWith("/lib/", StringComparison.Ordinal) ||
                Exempt.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var settings = ctx.RequestServices.GetRequiredService<AppSettingStore>();
            var mode = (Environment.GetEnvironmentVariable("AUTH_MODE")
                        ?? await settings.GetAsync(AppSetting.AuthMode, ctx.RequestAborted)
                        ?? "none").Trim().ToLowerInvariant();

            if (mode is "none" or "")
            {
                await next();
                return;
            }

            var expectedHash = Environment.GetEnvironmentVariable("AUTH_SECRET") is { Length: > 0 } envSecret
                ? Hash(envSecret)
                : await settings.GetAsync(AppSetting.AuthSecretHash, ctx.RequestAborted);

            if (string.IsNullOrEmpty(expectedHash))
            {
                // misconfigured — fail open rather than lock everyone out
                await next();
                return;
            }

            if (mode == "apikey" && ApiKeyOk(ctx, expectedHash))
            {
                await next();
                return;
            }
            if (mode == "basic" && BasicOk(ctx, expectedHash))
            {
                await next();
                return;
            }

            if (mode == "basic")
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"qbit-flow\"";
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Unauthorized");
        });

    private static bool ApiKeyOk(HttpContext ctx, string expectedHash)
    {
        var key = ctx.Request.Headers["X-Api-Key"].FirstOrDefault()
                  ?? ctx.Request.Query["apikey"].FirstOrDefault()
                  ?? ctx.Request.Cookies["qf_key"];
        if (string.IsNullOrEmpty(key) || !FixedEquals(Hash(key), expectedHash)) return false;

        if (ctx.Request.Query.ContainsKey("apikey"))
            ctx.Response.Cookies.Append("qf_key", key, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(30) });
        return true;
    }

    private static bool BasicOk(HttpContext ctx, string expectedHash)
    {
        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var sep = decoded.IndexOf(':');
            var user = sep >= 0 ? decoded[..sep] : "";
            var pass = sep >= 0 ? decoded[(sep + 1)..] : decoded;
            return FixedEquals(Hash(pass), expectedHash) || FixedEquals(Hash(user), expectedHash);
        }
        catch
        {
            return false;
        }
    }

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
