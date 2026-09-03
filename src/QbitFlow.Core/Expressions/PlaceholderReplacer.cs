using System.Collections;
using System.Text.RegularExpressions;

namespace QbitFlow.Core.Expressions;

/// <summary>
/// Substitutes <c>&lt;key&gt;</c> tokens in a criteria string with values from the evaluation context,
/// before the string is handed to DynamicExpresso. Ported from the legacy
/// <c>AutoTorrentRuleBase.Replacer</c> (the regex-first implementation).
/// </summary>
public static partial class PlaceholderReplacer
{
    [GeneratedRegex(@"<([^<>]+)>", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Replaces every <c>&lt;key&gt;</c> found in <paramref name="template"/>. An unknown key is
    /// replaced with an error marker so the resulting expression fails to evaluate (parity with the
    /// legacy behaviour) — unless <paramref name="throwOnMissing"/> is set.
    /// </summary>
    public static string Apply(
        string template,
        IReadOnlyDictionary<string, object?> context,
        bool throwOnMissing = false)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('<'))
            return template;

        return TokenRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;

            if (!context.TryGetValue(key, out var raw))
            {
                if (throwOnMissing)
                    throw new KeyNotFoundException($"Unknown field '<{key}>' in criteria.");
                return $"** ERROR <{key}> is not a key **";
            }

            return Stringify(raw);
        });
    }

    /// <summary>Names of every <c>&lt;key&gt;</c> referenced by a template.</summary>
    public static IReadOnlySet<string> ReferencedKeys(string template)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(template))
            foreach (Match m in TokenRegex().Matches(template))
                set.Add(m.Groups[1].Value);
        return set;
    }

    private static string Stringify(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "True" : "False",
        IEnumerable enumerable and not string =>
            string.Join(",", enumerable.Cast<object?>().Select(x => x?.ToString() ?? "")),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
