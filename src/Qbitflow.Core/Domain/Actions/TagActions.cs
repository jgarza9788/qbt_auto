namespace Qbitflow.Core.Domain.Actions;

/// <summary>Idempotency: skipped for a torrent that already has every listed tag.</summary>
public class AddTagsAction : ActionDefinition
{
    public required List<string> Tags { get; init; }
}

/// <summary>Idempotency: skipped for a torrent that has none of the listed tags.</summary>
public class RemoveTagsAction : ActionDefinition
{
    public required List<string> Tags { get; init; }
}
