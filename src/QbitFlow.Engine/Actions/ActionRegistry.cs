using QbitFlow.Core.Abstractions;

namespace QbitFlow.Engine.Actions;

/// <summary>
/// Resolves an <see cref="IActionHandler"/> by its <c>Type</c> key. Populated from DI — adding an
/// action is one new class implementing <see cref="IActionHandler"/>, zero edits here.
/// </summary>
public sealed class ActionRegistry
{
    private readonly IReadOnlyDictionary<string, IActionHandler> _byType;

    public ActionRegistry(IEnumerable<IActionHandler> handlers)
    {
        var map = new Dictionary<string, IActionHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.Type, handler))
                throw new InvalidOperationException($"Duplicate action handler for type '{handler.Type}'.");
        }
        _byType = map;
    }

    public bool TryGet(string type, out IActionHandler handler) => _byType.TryGetValue(type, out handler!);

    public IActionHandler Get(string type) =>
        _byType.TryGetValue(type, out var h)
            ? h
            : throw new KeyNotFoundException($"No action handler registered for type '{type}'.");

    public IEnumerable<IActionHandler> All => _byType.Values;
}
