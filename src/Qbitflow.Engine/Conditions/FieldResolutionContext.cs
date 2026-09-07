using Qbitflow.Core.Domain;

namespace Qbitflow.Engine.Conditions;

/// <summary>One configured source instance, reduced to what a field key needs to resolve against.</summary>
public sealed record InstanceRef(int Id, string Name, SourceType Type);

/// <summary>
/// What the compiler validates the <c>&lt;instance&gt;</c> segment of a field key against.
///
/// Instances are runtime configuration, so a rule saved on one install can name an instance that
/// does not exist on another. When the caller knows the configured instances it passes them here
/// and a typo becomes a clear compile error naming the real ones. When it does not -- unit tests,
/// and the bundled example rules, which ship to installs whose instance names cannot be known --
/// <see cref="Lenient"/> skips only that check: the type and field segments are still validated
/// against <see cref="SourceFieldCatalog"/>, so a bad key is still caught.
/// </summary>
public sealed record FieldResolutionContext(
    IReadOnlyList<InstanceRef> Instances,
    IReadOnlyList<string> StoragePathNames)
{
    /// <summary>False when instance names cannot be checked (no configured instances were supplied).</summary>
    public bool ValidatesInstances { get; init; } = true;

    public static readonly FieldResolutionContext Lenient =
        new([], []) { ValidatesInstances = false };

    public static FieldResolutionContext For(IEnumerable<InstanceRef> instances, IEnumerable<string> storagePathNames) =>
        new([.. instances], [.. storagePathNames]);

    /// <summary>Configured names for a type, used both to resolve a key and to build its error message.</summary>
    public IReadOnlyList<string> NamesFor(string typeKey) =>
        typeKey == SourceNaming.StorageTypeKey
            ? StoragePathNames
            : [.. Instances.Where(i => SourceNaming.TypeKey(i.Type) == typeKey).Select(i => i.Name)];

    /// <summary>The type a name is configured under, if any -- lets the compiler say "that is a plex instance".</summary>
    public string? TypeOf(string instanceName)
    {
        var match = Instances.FirstOrDefault(i => string.Equals(i.Name, instanceName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return SourceNaming.TypeKey(match.Type);
        }
        return StoragePathNames.Any(n => string.Equals(n, instanceName, StringComparison.OrdinalIgnoreCase))
            ? SourceNaming.StorageTypeKey
            : null;
    }
}
