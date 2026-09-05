using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Sources.Http;

public interface IInstanceHttpClientFactory
{
    HttpClient CreateClient(SourceConnectionInfo connection);
}

/// <summary>
/// Picks between two pre-configured named HttpClients (cert validation on/off) based on
/// the instance's VerifySsl flag. Registered by AddQbitflowSources.
/// </summary>
public class InstanceHttpClientFactory(IHttpClientFactory httpClientFactory) : IInstanceHttpClientFactory
{
    public const string SecureClientName = "Qbitflow.Source.Secure";
    public const string InsecureClientName = "Qbitflow.Source.Insecure";

    public HttpClient CreateClient(SourceConnectionInfo connection) =>
        httpClientFactory.CreateClient(connection.VerifySsl ? SecureClientName : InsecureClientName);
}
