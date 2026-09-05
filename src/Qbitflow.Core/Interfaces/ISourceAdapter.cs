using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Core.Interfaces;

/// <summary>
/// One data source's connector. A new source is added by implementing this interface
/// in a single file in Qbitflow.Sources and registering it in DI -- no other project
/// needs to change.
/// </summary>
public interface ISourceAdapter
{
    SourceType SourceType { get; }

    Task<ConnectionTestResult> TestConnectionAsync(SourceConnectionInfo connection, CancellationToken ct = default);

    Task<SourceFetchResult> FetchAsync(SourceConnectionInfo connection, CancellationToken ct = default);
}
