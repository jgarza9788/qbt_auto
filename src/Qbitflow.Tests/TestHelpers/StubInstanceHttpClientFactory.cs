using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Http;

namespace Qbitflow.Tests.TestHelpers;

internal sealed class StubInstanceHttpClientFactory(HttpMessageHandler handler) : IInstanceHttpClientFactory
{
    public HttpClient CreateClient(SourceConnectionInfo connection) => new(handler, disposeHandler: false);
}
