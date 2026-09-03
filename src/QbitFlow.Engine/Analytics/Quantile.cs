namespace QbitFlow.Engine.Analytics;

/// <summary>
/// Rank-normalisation of a set of counts to scores in <c>[0, 1]</c>. Ported verbatim (behaviour-wise)
/// from the legacy <c>Misc.NormalizeQuantile</c> / <c>Misc.Normalize</c>: sorted rank, ties share the
/// mid-rank, highest value → 1.0.
/// </summary>
public static class Quantile
{
    /// <summary>Quantile (rank) normalisation. Ties get the average rank of the tie block.</summary>
    public static IReadOnlyList<(string Key, double Score)> NormalizeQuantile(IReadOnlyDictionary<string, double> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Count == 0) return [];
        if (counts.Count == 1) return [(counts.Keys.First(), 1.0)];

        var items = counts.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Value).ToArray();
        var n = items.Length;
        var result = new List<(string, double)>(n);

        var i = 0;
        while (i < n)
        {
            var j = i;
            while (j + 1 < n && items[j + 1].Value.Equals(items[i].Value))
                j++;

            var midRank = (i + j) / 2.0;
            var score = midRank / (n - 1);

            for (var k = i; k <= j; k++)
                result.Add((items[k].Key, score));

            i = j + 1;
        }

        return result;
    }

    /// <summary>Plain min-max normalisation (all-equal → 1.0).</summary>
    public static IReadOnlyList<(string Key, double Score)> NormalizeMinMax(IReadOnlyDictionary<string, double> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Count == 0) return [];

        var min = counts.Values.Min();
        var max = counts.Values.Max();

        return counts
            .Select(kv => (kv.Key, Score: max.Equals(min) ? 1.0 : (kv.Value - min) / (max - min)))
            .ToArray();
    }
}
