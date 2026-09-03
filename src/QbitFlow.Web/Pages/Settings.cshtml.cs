using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Config;

namespace QbitFlow.Web.Pages;

public class SettingsModel(AppSettingStore settings) : PageModel
{
    [BindProperty] public int AnalyticsIntervalMinutes { get; set; }
    [BindProperty] public string AnalyticsWeights { get; set; } = "";
    [BindProperty] public int QbtFreshnessSeconds { get; set; }
    [BindProperty] public string AuthMode { get; set; } = "none";
    [BindProperty] public string? AuthSecret { get; set; }

    public string SecretsEncryption { get; private set; } = "none";
    public bool AuthModeFromEnv { get; private set; }
    public bool AuthSecretSet { get; private set; }
    public Dictionary<string, string?> EnvStatus { get; private set; } = [];

    public async Task OnGetAsync()
    {
        AnalyticsIntervalMinutes = await settings.GetIntAsync(AppSetting.AnalyticsIntervalMinutes, 360);
        AnalyticsWeights = await settings.GetAsync(AppSetting.AnalyticsWeights) ?? "(defaults: all .01 / year .5 / month .9 / week 1.0)";
        QbtFreshnessSeconds = await settings.GetIntAsync(AppSetting.QbtFreshnessSeconds, 60);
        SecretsEncryption = Environment.GetEnvironmentVariable("SECRETS_ENCRYPTION") ?? "none";

        var envMode = Environment.GetEnvironmentVariable("AUTH_MODE");
        AuthModeFromEnv = !string.IsNullOrWhiteSpace(envMode);
        AuthMode = (envMode ?? await settings.GetAsync(AppSetting.AuthMode) ?? "none").ToLowerInvariant();
        AuthSecretSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTH_SECRET"))
                        || !string.IsNullOrEmpty(await settings.GetAsync(AppSetting.AuthSecretHash));

        foreach (var k in new[] { "QBT_URL", "QBT_USER", "QBT_PWD", "PLEX_URL", "PLEX_TOKEN", "JELLYFIN_URL", "CONFIG_IMPORT_PATH", "EXPORTS_DIR", "SECRETS_KEY_DIR" })
            EnvStatus[k] = string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)) ? null : "set";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await settings.SetAsync(AppSetting.AnalyticsIntervalMinutes, Math.Max(5, AnalyticsIntervalMinutes).ToString());
        await settings.SetAsync(AppSetting.QbtFreshnessSeconds, Math.Max(5, QbtFreshnessSeconds).ToString());
        if (!string.IsNullOrWhiteSpace(AnalyticsWeights) && AnalyticsWeights.TrimStart().StartsWith('{'))
            await settings.SetAsync(AppSetting.AnalyticsWeights, AnalyticsWeights.Trim());

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTH_MODE")))
        {
            var mode = AuthMode is "apikey" or "basic" ? AuthMode : "none";
            await settings.SetAsync(AppSetting.AuthMode, mode);
            if (!string.IsNullOrEmpty(AuthSecret))
                await settings.SetAsync(AppSetting.AuthSecretHash, Startup.AuthGate.Hash(AuthSecret));
        }

        TempData["Msg"] = "Settings saved.";
        return RedirectToPage();
    }
}
