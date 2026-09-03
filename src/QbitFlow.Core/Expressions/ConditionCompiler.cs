using System.Globalization;
using System.Text;
using QbitFlow.Core.Domain;

namespace QbitFlow.Core.Expressions;

public sealed record CompileResult(string Expression, bool Valid, IReadOnlyList<string> Errors)
{
    public static CompileResult Ok(string expression) => new(expression, true, []);
    public static CompileResult Fail(params string[] errors) => new("false", false, errors);
}

/// <summary>
/// Turns a structured <see cref="RuleConditionGroup"/> tree into the DynamicExpresso string that
/// <see cref="CriteriaEvaluator"/> consumes — exactly the style the legacy criteria already use.
/// </summary>
public sealed class ConditionCompiler
{
    private readonly CriteriaEvaluator _evaluator;

    public ConditionCompiler(CriteriaEvaluator? evaluator = null) => _evaluator = evaluator ?? new CriteriaEvaluator();

    public CompileResult Compile(RuleConditionGroup? root)
    {
        if (root is null)
            return CompileResult.Ok("true");

        var errors = new List<string>();
        var expr = EmitGroup(root, errors);
        if (errors.Count > 0)
            return CompileResult.Fail([.. errors]);

        var (ok, error) = _evaluator.Validate(expr);
        return ok ? CompileResult.Ok(expr) : new CompileResult(expr, false, [error ?? "invalid expression"]);
    }

    private static string EmitGroup(RuleConditionGroup group, List<string> errors)
    {
        var parts = new List<string>();

        foreach (var condition in group.Conditions.OrderBy(c => c.Order))
            parts.Add(EmitCondition(condition, errors));

        foreach (var child in group.Children.OrderBy(g => g.Order))
            parts.Add(EmitGroup(child, errors));

        if (parts.Count == 0)
            return "true";

        var op = group.Logic == ConditionLogic.And ? " && " : " || ";
        return "(" + string.Join(op, parts) + ")";
    }

    private static string EmitCondition(RuleCondition c, List<string> errors)
    {
        var field = c.Field;
        var type = FieldCatalog.TypeOf(field);
        var numeric = type is FieldType.Number or FieldType.Duration;
        var token = $"<{field}>";
        var quoted = $"\"{token}\"";

        string Num()
        {
            if (double.TryParse(c.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d.ToString("R", CultureInfo.InvariantCulture);
            errors.Add($"'{field}': '{c.Value}' is not a number.");
            return "0";
        }

        string Str() => "\"" + c.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string Rx() => "\"" + c.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        return c.Operator switch
        {
            ConditionOperator.Eq  => numeric ? $"{token} == {Num()}" : $"{quoted} == {Str()}",
            ConditionOperator.Neq => numeric ? $"{token} != {Num()}" : $"{quoted} != {Str()}",
            ConditionOperator.Gt  => $"{token} > {Num()}",
            ConditionOperator.Gte => $"{token} >= {Num()}",
            ConditionOperator.Lt  => $"{token} < {Num()}",
            ConditionOperator.Lte => $"{token} <= {Num()}",

            ConditionOperator.Contains    => $"contains({quoted}, {Str()})",
            ConditionOperator.NotContains => $"!contains({quoted}, {Str()})",
            ConditionOperator.Matches     => $"match({quoted}, {Rx()})",
            ConditionOperator.NotMatches  => $"!match({quoted}, {Rx()})",

            ConditionOperator.InList => EmitInList(c, field, token, quoted, numeric, errors),

            ConditionOperator.DaysAgoGte => $"daysAgo({quoted}) >= {Num()}",
            ConditionOperator.DaysAgoLt  => $"daysAgo({quoted}) < {Num()}",

            ConditionOperator.IsTrue  => $"{token} == True",
            ConditionOperator.IsFalse => $"{token} == False",

            _ => Unknown(c, errors),
        };
    }

    private static string EmitInList(RuleCondition c, string field, string token, string quoted, bool numeric, List<string> errors)
    {
        var items = c.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0)
        {
            errors.Add($"'{field}': InList needs at least one comma-separated value.");
            return "false";
        }

        var sb = new StringBuilder("(");
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(" || ");
            if (numeric)
            {
                sb.Append(token).Append(" == ");
                sb.Append(double.TryParse(items[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString("R", CultureInfo.InvariantCulture)
                    : "0");
            }
            else
            {
                var lit = "\"" + items[i].Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                sb.Append("contains(").Append(quoted).Append(", ").Append(lit).Append(')');
            }
        }
        return sb.Append(')').ToString();
    }

    private static string Unknown(RuleCondition c, List<string> errors)
    {
        errors.Add($"Unsupported operator {c.Operator} on '{c.Field}'.");
        return "false";
    }
}
