using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Engine.Conditions;

/// <summary>
/// Builds the <see cref="FieldResolutionContext"/> a compile needs from what the user has actually
/// configured, so both the rule editor and the scheduled runner validate field keys the same way.
/// </summary>
public interface IFieldContextProvider
{
    Task<FieldResolutionContext> GetAsync(CancellationToken ct = default);
}

public class FieldContextProvider(AppDbContext db) : IFieldContextProvider
{
    public async Task<FieldResolutionContext> GetAsync(CancellationToken ct = default)
    {
        var instances = await db.Instances.AsNoTracking()
            .Select(i => new { i.Id, i.Name, i.SourceType })
            .ToListAsync(ct);
        var storagePathNames = await db.StoragePaths.AsNoTracking().Select(s => s.Name).ToListAsync(ct);

        return FieldResolutionContext.For(
            instances.Select(i => new InstanceRef(i.Id, i.Name, i.SourceType)),
            storagePathNames);
    }

    /// <summary>Builds a context from lists a caller already has in hand, avoiding a second round-trip.</summary>
    public static FieldResolutionContext From(IEnumerable<Instance> instances, IEnumerable<StoragePathConfig> storagePaths) =>
        FieldResolutionContext.For(
            instances.Select(i => new InstanceRef(i.Id, i.Name, i.SourceType)),
            storagePaths.Select(s => s.Name));
}
