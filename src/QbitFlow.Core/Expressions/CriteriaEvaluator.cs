using System.Globalization;
using System.Text.RegularExpressions;
using DynamicExpresso;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace QbitFlow.Core.Expressions;

/// <summary>
/// Evaluates a rule's criteria string against an evaluation context. Ported from the legacy
/// <c>AutoTorrentRuleBase.Evaluate</c> — same <c>bool?</c> contract (true → act-on-true,
/// false → act-on-false, null → skip) and the same helper functions
/// (<c>contains</c>, <c>match</c>, <c>daysAgo</c>). The interpreter is built once and treated as
/// immutable; only <c>Eval</c> is called after construction.
/// </summary>
public sealed class CriteriaEvaluator
{
    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(2);

    private readonly Interpreter _interpreter;
    private readonly ILogger _log;

    public CriteriaEvaluator(ILogger<CriteriaEvaluator>? log = null)
    {
        _log = log ?? NullLogger<CriteriaEvaluator>.Instance;

        // NOTE: no .Reference(...) of any assembly/type — keep the expression surface minimal.
        _interpreter = new Interpreter(InterpreterOptions.Default)
            .SetFunction("contains", (string text, string sub) =>
                text.Contains(sub, StringComparison.Ordinal))
            .SetFunction("match", (string text, string pattern) =>
                Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, EvalTimeout))
            .SetFunction("daysAgo", (string iso) =>
                (DateTime.UtcNow - DateTime.Parse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)).TotalDays)
            .SetDefaultNumberType(DefaultNumberType.Double);
    }

    /// <summary>
    /// Substitutes placeholders, evaluates the boolean expression, and returns the result.
    /// Any parse/eval error (or timeout) yields <c>null</c> — the rule is skipped for that torrent.
    /// </summary>
    public bool? Evaluate(string criteria, IReadOnlyDictionary<string, object?> context, string? logContext = null)
    {
        string expression;
        try
        {
            expression = PlaceholderReplacer.Apply(criteria, context);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Placeholder substitution failed. {LogContext}", logContext);
            return null;
        }

        try
        {
            var task = Task.Run(() => _interpreter.Eval<bool>(expression));
            if (!task.Wait(EvalTimeout))
            {
                _log.LogWarning("Criteria evaluation timed out: {Expression} {LogContext}", expression, logContext);
                return null;
            }
            return task.Result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Criteria evaluation failed: {Expression} {LogContext}", expression, logContext);
            return null;
        }
    }

    /// <summary>Compile-checks an expression against a synthetic context (every referenced key present).</summary>
    public (bool Ok, string? Error) Validate(string criteria)
    {
        try
        {
            var synthetic = PlaceholderReplacer.ReferencedKeys(criteria)
                .ToDictionary(k => k, object? (_) => (object?)0);
            var expression = PlaceholderReplacer.Apply(criteria, synthetic, throwOnMissing: false);
            _interpreter.Parse(expression);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
