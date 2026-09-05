using System.Text.Json;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Engine.Conditions;
using Xunit;

namespace Qbitflow.Tests.Engine;

/// <summary>
/// Regression coverage for a real bug found manually testing the Rule editor: System.Text.Json
/// serializes enums as integers by default, so a ConditionNode tree serialized to JSON (as
/// Rule.ConditionTreeJson is) failed to deserialize -- "Operator":"And" could not convert to
/// LogicalOperator. ConditionSqlCompilerTests never caught this because it builds trees directly
/// in C#, never round-tripping through JSON text like RuleRunner and the Rule editor both do.
/// </summary>
public class ConditionNodeJsonRoundTripTests
{
    [Fact]
    public void GroupNode_RoundTripsThroughJson_WithStringOperators()
    {
        var tree = new GroupNode
        {
            Operator = LogicalOperator.And,
            Children =
            [
                new ComparisonNode { Field = "category", Operator = ComparisonOperator.Eq, Value = JsonSerializer.SerializeToElement("linux") }
            ]
        };

        var json = JsonSerializer.Serialize<ConditionNode>(tree);

        Assert.Contains("\"Operator\":\"And\"", json);
        Assert.Contains("\"Operator\":\"Eq\"", json);

        var roundTripped = JsonSerializer.Deserialize<ConditionNode>(json);
        var group = Assert.IsType<GroupNode>(roundTripped);
        Assert.Equal(LogicalOperator.And, group.Operator);
        var comparison = Assert.IsType<ComparisonNode>(Assert.Single(group.Children));
        Assert.Equal(ComparisonOperator.Eq, comparison.Operator);
    }

    [Fact]
    public void FullTree_RoundTripsThroughJson_AndStillCompiles()
    {
        var tree = new GroupNode
        {
            Operator = LogicalOperator.And,
            Children =
            [
                new ComparisonNode { Field = "category", Operator = ComparisonOperator.Eq, Value = JsonSerializer.SerializeToElement("linux") },
                new NotNode { Child = new ComparisonNode { Field = "state", Operator = ComparisonOperator.Eq, Value = JsonSerializer.SerializeToElement("error") } },
                new ExistsNode
                {
                    Relation = "watch_history",
                    Negate = true,
                    Condition = new ComparisonNode { Field = "days_since_watched", Operator = ComparisonOperator.Lte, Value = JsonSerializer.SerializeToElement(90.0) }
                }
            ]
        };

        var json = JsonSerializer.Serialize<ConditionNode>(tree);
        var roundTripped = JsonSerializer.Deserialize<ConditionNode>(json);

        Assert.NotNull(roundTripped);

        // The real end-to-end assertion: the round-tripped tree must still compile to SQL
        // exactly like the original (this is what a rule saved through the editor, then
        // loaded and run by RuleRunner, actually goes through).
        var compiler = new ConditionSqlCompiler();
        var originalSql = compiler.Compile(tree).Sql;
        var roundTrippedSql = compiler.Compile(roundTripped!).Sql;
        Assert.Equal(originalSql, roundTrippedSql);
    }

    [Fact]
    public void RuleEditorStyleJson_FromRawStrings_Deserializes()
    {
        // Exactly the shape the visual builder's JS emits (see rule-editor.js) -- a plain
        // JSON string, not built via C# serialization, to catch any shape mismatch.
        const string json = """
            {"kind":"group","Operator":"And","Children":[
                {"kind":"comparison","Field":"category","Operator":"Eq","Value":"linux"},
                {"kind":"exists","Relation":"watch_history","Negate":true,"Condition":{"kind":"comparison","Field":"days_since_watched","Operator":"Lte","Value":90}}
            ]}
            """;

        var tree = JsonSerializer.Deserialize<ConditionNode>(json);
        Assert.NotNull(tree);

        var compiler = new ConditionSqlCompiler();
        var compiled = compiler.Compile(tree!);

        Assert.Contains("NOT EXISTS", compiled.Sql);
        Assert.Equal("linux", compiled.Parameters["$p0"]);
    }
}
