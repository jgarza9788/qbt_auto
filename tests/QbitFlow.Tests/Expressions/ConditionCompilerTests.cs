using FluentAssertions;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Expressions;

namespace QbitFlow.Tests.Expressions;

public class ConditionCompilerTests
{
    private readonly ConditionCompiler _compiler = new();

    private static RuleConditionGroup Group(ConditionLogic logic, params RuleCondition[] conditions)
    {
        var g = new RuleConditionGroup { Logic = logic };
        for (var i = 0; i < conditions.Length; i++) { conditions[i].Order = i; g.Conditions.Add(conditions[i]); }
        return g;
    }

    private static RuleCondition C(string field, ConditionOperator op, string value) =>
        new() { Field = field, Operator = op, Value = value };

    [Theory]
    [InlineData("Size", ConditionOperator.Lt, "1073741824", "(<Size> < 1073741824)")]
    [InlineData("Size", ConditionOperator.Gte, "10", "(<Size> >= 10)")]
    [InlineData("Category", ConditionOperator.Eq, "Movies", "(\"<Category>\" == \"Movies\")")]
    [InlineData("Name", ConditionOperator.Contains, "1080p", "(contains(\"<Name>\", \"1080p\"))")]
    [InlineData("Name", ConditionOperator.Matches, "S[0-9][0-9]E[0-9][0-9]", "(match(\"<Name>\", \"S[0-9][0-9]E[0-9][0-9]\"))")]
    [InlineData("LastActivityTime", ConditionOperator.DaysAgoGte, "30", "(daysAgo(\"<LastActivityTime>\") >= 30)")]
    public void Emits_expected_expression(string field, ConditionOperator op, string value, string expected)
    {
        var result = _compiler.Compile(Group(ConditionLogic.And, C(field, op, value)));
        result.Valid.Should().BeTrue(string.Join(";", result.Errors));
        result.Expression.Should().Be(expected);
    }

    [Fact]
    public void Nested_groups_use_the_right_combinator()
    {
        var inner = Group(ConditionLogic.Or, C("Category", ConditionOperator.Eq, "Movies"), C("Category", ConditionOperator.Eq, "Shows"));
        var root = Group(ConditionLogic.And, C("Progress", ConditionOperator.Eq, "1"));
        inner.Order = 1;
        root.Children.Add(inner);

        var result = _compiler.Compile(root);

        result.Valid.Should().BeTrue();
        result.Expression.Should().Be("(<Progress> == 1 && (\"<Category>\" == \"Movies\" || \"<Category>\" == \"Shows\"))");
    }

    [Fact]
    public void Null_root_compiles_to_true()
    {
        _compiler.Compile(null).Expression.Should().Be("true");
    }
}
