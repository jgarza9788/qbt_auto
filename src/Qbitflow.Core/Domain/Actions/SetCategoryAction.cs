namespace Qbitflow.Core.Domain.Actions;

/// <summary>Idempotency: skipped for a torrent whose category already equals Category.</summary>
public class SetCategoryAction : ActionDefinition
{
    public required string Category { get; init; }
}
