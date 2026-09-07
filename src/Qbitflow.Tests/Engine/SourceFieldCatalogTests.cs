using System.Text.Json;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Engine.Conditions;
using Qbitflow.Snapshot;
using Xunit;

namespace Qbitflow.Tests.Engine;

/// <summary>
/// The catalog and the snapshot DDL are written in two different projects and nothing but these
/// tests stops them drifting apart. Every documented field is compiled and actually executed
/// against a real (empty) snapshot, so a column renamed on one side fails here rather than at a
/// user's next scheduled run.
/// </summary>
public class SourceFieldCatalogTests : IDisposable
{
    private readonly SnapshotDatabase _db = new();
    private readonly ConditionSqlCompiler _compiler = new();

    public void Dispose() => _db.Dispose();

    public static TheoryData<string, string> EveryField()
    {
        var data = new TheoryData<string, string>();
        foreach (var type in SourceFieldCatalog.Types.Values)
        {
            foreach (var field in type.Fields.Values)
            {
                data.Add(type.TypeKey, field.Key);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryField))]
    public async Task EveryDocumentedField_CompilesAndRunsAgainstTheSnapshot(string typeKey, string fieldKey)
    {
        var field = SourceFieldCatalog.Types[typeKey].Fields[fieldKey];
        var node = new ComparisonNode
        {
            Field = $"{typeKey}.*.{fieldKey}",
            Operator = ComparisonOperator.IsNotNull
        };

        var query = _compiler.Compile(node);
        var matches = await _compiler.ExecuteAsync(_db, query);

        Assert.Empty(matches);
        Assert.NotNull(field.Description);
    }

    [Fact]
    public void EveryEnumSourceType_HasACatalogEntry()
    {
        foreach (var type in Enum.GetValues<SourceType>())
        {
            Assert.True(SourceFieldCatalog.Types.ContainsKey(SourceNaming.TypeKey(type)),
                $"{type} has no catalog entry -- a rule could never address it.");
        }

        Assert.True(SourceFieldCatalog.Types.ContainsKey(SourceNaming.StorageTypeKey));
    }

    [Fact]
    public void EveryFieldIsEitherARowFieldOrAnAggregate_NeverBoth()
    {
        foreach (var type in SourceFieldCatalog.Types.Values)
        {
            foreach (var field in type.Fields.Values)
            {
                Assert.True(
                    field.RowExpression is null ^ field.AggregateExpression is null,
                    $"{type.TypeKey}.{field.Key} must define exactly one of RowExpression / AggregateExpression.");
            }
        }
    }

    [Fact]
    public void OnlyCorrelatedSourcesExposeAggregates()
    {
        foreach (var type in SourceFieldCatalog.Types.Values.Where(t => t.Correlation != SourceCorrelation.PathKey))
        {
            Assert.DoesNotContain(type.Fields.Values, f => f.IsAggregate);
        }
    }

    [Fact]
    public void AllMediaHistoryTypes_ShareTheSameFieldVocabulary()
    {
        var expected = SourceFieldCatalog.Types[SourceNaming.TypeKey(SourceType.Jellyfin)].Fields.Keys.Order().ToList();

        foreach (var type in SourceNaming.MediaHistoryTypes)
        {
            Assert.Equal(expected, SourceFieldCatalog.Types[SourceNaming.TypeKey(type)].Fields.Keys.Order().ToList());
        }
    }

    [Fact]
    public void EveryTypeGetsItsOwnAlias_SoTwoSourcesInOneRuleCannotCollide()
    {
        // Aliases are suffixed with a counter at compile time, but two types sharing a prefix
        // would still be confusing to read in the compiled SQL preview.
        var prefixes = SourceFieldCatalog.Types.Values.Select(t => t.AliasPrefix).ToList();
        Assert.Equal(prefixes.Count, prefixes.Distinct().Count());
    }

    [Fact]
    public void ExampleRules_UseOnlyThreeSegmentKeys()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "example-rules.json"));
        using var doc = JsonDocument.Parse(json);

        foreach (var rule in doc.RootElement.GetProperty("Rules").EnumerateArray())
        {
            var tree = rule.GetProperty("ConditionTreeJson").GetString()!;
            foreach (var field in FieldsIn(JsonDocument.Parse(tree).RootElement))
            {
                Assert.True(FieldKey.TryParse(field, out var key, out var error), error);

                // Examples ship to installs whose instance names can't be known in advance, so
                // they address any instance. Storage is the exception: a path name is part of the
                // question being asked ("is /downloads full?"), and the example says to create it.
                Assert.True(key.IsWildcardInstance || key.Type == SourceNaming.StorageTypeKey,
                    $"Example rule '{rule.GetProperty("Name").GetString()}' names instance '{key.Instance}'; use '*'.");
            }
        }
    }


    [Fact]
    public void ExampleRules_UseOnlyShapesTheVisualBuilderCanRender()
    {
        // The builder's root is always an AND/OR group and it renders comparison / exists /
        // group rows only. A shipped example outside that vocabulary would open as an empty or
        // silently altered condition -- worse than not shipping it.
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "example-rules.json"));
        using var doc = JsonDocument.Parse(json);

        foreach (var rule in doc.RootElement.GetProperty("Rules").EnumerateArray())
        {
            var name = rule.GetProperty("Name").GetString();
            var tree = JsonDocument.Parse(rule.GetProperty("ConditionTreeJson").GetString()!).RootElement;

            Assert.Equal("group", tree.GetProperty("kind").GetString());
            AssertRenderable(tree, name!);
        }
    }

    private static void AssertRenderable(JsonElement node, string ruleName)
    {
        var kind = node.GetProperty("kind").GetString();
        Assert.True(kind is "group" or "comparison" or "exists",
            $"Example rule '{ruleName}' uses a '{kind}' node, which the visual builder cannot render.");

        if (node.TryGetProperty("Children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                AssertRenderable(child, ruleName);
            }
        }
        if (node.TryGetProperty("Condition", out var condition))
        {
            AssertRenderable(condition, ruleName);
        }
    }

    private static IEnumerable<string> FieldsIn(JsonElement node)
    {
        if (node.TryGetProperty("Field", out var field))
        {
            yield return field.GetString()!;
        }
        foreach (var name in new[] { "Condition", "Child" })
        {
            if (node.TryGetProperty(name, out var child))
            {
                foreach (var f in FieldsIn(child))
                {
                    yield return f;
                }
            }
        }
        if (node.TryGetProperty("Children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                foreach (var f in FieldsIn(child))
                {
                    yield return f;
                }
            }
        }
    }
}
