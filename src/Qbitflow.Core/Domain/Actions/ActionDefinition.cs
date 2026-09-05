using System.Text.Json.Serialization;

namespace Qbitflow.Core.Domain.Actions;

/// <summary>
/// One action a Rule performs against every torrent it matched. Stored as Rule.ActionsJson
/// (a JSON array of these). Qbitflow.Engine's ActionExecutor is the only thing that turns
/// these into qBittorrent API calls, and it always checks current state first -- an
/// action whose desired state is already in place is skipped, never reapplied blindly.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(AddTagsAction), "add_tags")]
[JsonDerivedType(typeof(RemoveTagsAction), "remove_tags")]
[JsonDerivedType(typeof(SetCategoryAction), "set_category")]
[JsonDerivedType(typeof(MoveAction), "move")]
[JsonDerivedType(typeof(SetUploadLimitAction), "set_upload_limit")]
[JsonDerivedType(typeof(SetDownloadLimitAction), "set_download_limit")]
public abstract class ActionDefinition
{
}
