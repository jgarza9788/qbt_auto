namespace QbitFlow.Sources.Qbt;

/// <summary>Everything needed to talk to one qBittorrent WebUI, resolved from a <c>SourceConnection</c>.</summary>
public sealed record QbtConnectionInfo(
    Guid SourceId,
    Uri BaseUrl,
    string Username,
    string Password,
    bool VerifyTls = true,
    int HttpTimeoutSeconds = 30);
