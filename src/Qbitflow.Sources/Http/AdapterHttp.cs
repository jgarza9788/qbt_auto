using System.Net;

namespace Qbitflow.Sources.Http;

internal static class AdapterHttp
{
    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, but throws an
    /// <see cref="InvalidOperationException"/> carrying the service name, the HTTP status, a
    /// short response-body snippet and a credentials hint for 401/403 -- so a failed
    /// connection test / run reads "Jellyfin returned HTTP 401 ..." instead of the framework's
    /// opaque "Response status code does not indicate success: 401 (Unauthorized)".
    /// </summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string service, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string snippet;
        try
        {
            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            snippet = text.Length <= 300 ? text : text[..300];
        }
        catch
        {
            snippet = string.Empty;
        }

        var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? " Check the API key / token configured for this instance."
            : string.Empty;

        // Skip the body snippet when it's just the status text repeated (e.g. "Unauthorized").
        var detail = string.IsNullOrEmpty(snippet) || string.Equals(snippet, response.StatusCode.ToString(), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" {snippet}";

        throw new InvalidOperationException(
            $"{service} returned HTTP {(int)response.StatusCode} {response.StatusCode}." + detail + hint);
    }
}
