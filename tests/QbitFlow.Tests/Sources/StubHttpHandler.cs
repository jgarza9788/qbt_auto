using System.Net;

namespace QbitFlow.Tests.Sources;

/// <summary>Routes requests to canned responses by a substring match on the absolute URI.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly List<(string Match, string Body, string ContentType, HttpStatusCode Code)> _routes = [];
    public List<string> Requests { get; } = [];

    public StubHttpHandler Add(string match, string body, string contentType = "application/json", HttpStatusCode code = HttpStatusCode.OK)
    {
        _routes.Add((match, body, contentType, code));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.AbsoluteUri;
        Requests.Add(uri);

        foreach (var (match, body, contentType, code) in _routes)
        {
            if (uri.Contains(match, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(code)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
                });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"no stub for {uri}"),
        });
    }
}
