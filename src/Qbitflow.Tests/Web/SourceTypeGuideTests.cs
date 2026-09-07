using System.Text.Json;
using Qbitflow.Core.Domain;
using Qbitflow.Web.Pages.Instances;
using Xunit;

namespace Qbitflow.Tests.Web;

/// <summary>
/// The instance form's help text is written by hand, so the thing worth guarding is that it stays
/// complete and reachable: every source type has copy, and the JSON the page hands Alpine is shaped
/// the way the bindings read it.
/// </summary>
public class SourceTypeGuideTests
{
    [Fact]
    public void EverySourceType_HasHelpText()
    {
        foreach (var type in Enum.GetValues<SourceType>())
        {
            Assert.True(SourceTypeGuide.ForType.ContainsKey(type),
                $"{type} has no entry in SourceTypeGuide, so its instance form would have no hints.");
        }
    }

    [Fact]
    public void NoHelpTextIsBlank()
    {
        foreach (var (type, guide) in SourceTypeGuide.ForType)
        {
            Assert.False(string.IsNullOrWhiteSpace(guide.Label), $"{type}: Label");
            Assert.False(string.IsNullOrWhiteSpace(guide.BaseUrl), $"{type}: BaseUrl");
            Assert.False(string.IsNullOrWhiteSpace(guide.BaseUrlPlaceholder), $"{type}: BaseUrlPlaceholder");
            Assert.False(string.IsNullOrWhiteSpace(guide.Username), $"{type}: Username");
            Assert.False(string.IsNullOrWhiteSpace(guide.Password), $"{type}: Password");
            Assert.False(string.IsNullOrWhiteSpace(guide.ApiKey), $"{type}: ApiKey");
            Assert.False(string.IsNullOrWhiteSpace(guide.ExtraConfig), $"{type}: ExtraConfig");
        }
    }

    [Fact]
    public void HelpTextDoesNotNameASourceTypeThatNoLongerExists()
    {
        var known = Enum.GetNames<SourceType>();

        foreach (var (type, guide) in SourceTypeGuide.ForType)
        {
            var copy = string.Join(" ", guide.Label, guide.BaseUrl, guide.Username, guide.Password,
                guide.ApiKey, guide.ExtraConfig);

            Assert.DoesNotContain("Streamystats", copy, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(known, k => copy.Contains(k, StringComparison.OrdinalIgnoreCase)
                                        || type == SourceType.Qbittorrent);
        }
    }

    [Fact]
    public void SerializedGuide_IsKeyedByEnumNameWithCamelCaseFields()
    {
        // The page binds these as guide.baseUrl / guide.apiKey and keys the map by the <select>'s
        // value, which is the enum name -- both halves of that contract are asserted here because
        // getting either wrong shows up as silently blank hints rather than an error.
        var json = JsonSerializer.Serialize(
            SourceTypeGuide.ForType.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var doc = JsonDocument.Parse(json);
        var jellyfin = doc.RootElement.GetProperty(nameof(SourceType.Jellyfin));

        Assert.True(jellyfin.TryGetProperty("baseUrl", out _));
        Assert.True(jellyfin.TryGetProperty("baseUrlPlaceholder", out _));
        Assert.True(jellyfin.TryGetProperty("apiKey", out _));
        Assert.True(jellyfin.TryGetProperty("extraConfig", out _));
        Assert.True(jellyfin.TryGetProperty("extraConfigPlaceholder", out _));
    }
}
