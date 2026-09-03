using QbitFlow.Core.Expressions;
using QbitFlow.Engine.Actions;

namespace QbitFlow.Web.Api;

internal static class MetaApi
{
    public static void MapMetaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/fields", () => Results.Json(
            FieldCatalog.All.Select(f => new
            {
                key = f.Key,
                placeholder = f.Placeholder,
                type = f.Type.ToString(),
                source = f.Source.ToString(),
                description = f.Description,
                sample = f.Sample,
            })));

        api.MapGet("/actions", (ActionRegistry registry) => Results.Json(
            registry.All.Select(h => new
            {
                type = h.Type,
                displayName = h.Schema.DisplayName,
                @params = h.Schema.Params.Select(p => new
                {
                    p.Key, p.Label, p.Kind, p.Required, p.Help,
                }),
            }).OrderBy(x => x.type)));
    }
}
