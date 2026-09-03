namespace QbitFlow.Core.Domain;

public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";

    // Well-known keys.
    public const string ConfigImportHash = "ConfigImportHash";
    public const string AnalyticsWeights = "AnalyticsWeights";
    public const string AnalyticsIntervalMinutes = "AnalyticsIntervalMinutes";
    public const string AnalyticsStaleAfterMinutes = "AnalyticsStaleAfterMinutes";
    public const string QbtFreshnessSeconds = "QbtFreshnessSeconds";
    public const string SecretsEncryption = "SecretsEncryption";
    public const string AuthMode = "AuthMode";
    public const string AuthSecretHash = "AuthSecretHash";
    public const string SchemaVersion = "SchemaVersion";
}
