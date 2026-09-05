using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Qbitflow.Infrastructure.Config;

/// <summary>Format-agnostic (de)serialization shared by config and rule import/export.</summary>
internal static class ConfigSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Serialize<T>(T value, ConfigFormat format) => format switch
    {
        ConfigFormat.Json => JsonSerializer.Serialize(value, JsonOptions),
        ConfigFormat.Yaml => YamlSerializer.Serialize(value!),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static T Deserialize<T>(string content, ConfigFormat format) => format switch
    {
        ConfigFormat.Json => JsonSerializer.Deserialize<T>(content, JsonOptions)
            ?? throw new InvalidOperationException("Empty or invalid JSON content."),
        ConfigFormat.Yaml => YamlDeserializer.Deserialize<T>(content)
            ?? throw new InvalidOperationException("Empty or invalid YAML content."),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
