using Qbitflow.Core.Domain;
using Qbitflow.Core.Interfaces;

namespace Qbitflow.Sources.Adapters;

public interface ISourceAdapterResolver
{
    ISourceAdapter Resolve(SourceType type);
}

public class SourceAdapterResolver : ISourceAdapterResolver
{
    private readonly Dictionary<SourceType, ISourceAdapter> _byType;

    public SourceAdapterResolver(IEnumerable<ISourceAdapter> adapters)
    {
        _byType = adapters.ToDictionary(a => a.SourceType);
    }

    public ISourceAdapter Resolve(SourceType type) =>
        _byType.TryGetValue(type, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"No adapter registered for source type {type}.");
}
