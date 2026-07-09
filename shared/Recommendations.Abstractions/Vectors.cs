namespace Recommendations.Abstractions;

/// <summary>Small vector utilities shared by recommenders: cosine, weighted centroid, and a light k-means
/// for building multiple taste centroids (niches) instead of one averaged-into-mush centroid.</summary>
public static class Vectors
{
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na <= 0 || nb <= 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>Blended cosine over two INDEPENDENT embedding spaces (e.g. visual feature + visual semantic),
    /// computed as a weighted average over whichever spaces are present for both the candidate and the
    /// centroid. The spaces are kept separate — they have different dimensions and must never be concatenated
    /// or mixed in a single k-means. Falls back gracefully: a candidate with only one space is scored on that
    /// space alone. Returns 0 when neither space is comparable.</summary>
    public static double BlendedCosine(float[]? candA, float[]? candB, float[]? centA, float[]? centB, double weightA, double weightB)
    {
        double num = 0, den = 0;
        if (candA is not null && centA is not null && weightA > 0) { num += weightA * Cosine(candA, centA); den += weightA; }
        if (candB is not null && centB is not null && weightB > 0) { num += weightB * Cosine(candB, centB); den += weightB; }
        return den > 0 ? num / den : 0;
    }

    public static float[]? WeightedCentroid(IEnumerable<(float[]? vec, double weight)> items)
    {
        float[]? acc = null;
        double total = 0;
        foreach (var (vec, weight) in items)
        {
            if (vec is null || weight <= 0)
                continue;
            acc ??= new float[vec.Length];
            if (vec.Length != acc.Length)
                continue;
            for (var i = 0; i < vec.Length; i++)
                acc[i] += (float)(vec[i] * weight);
            total += weight;
        }
        if (acc is null || total <= 0)
            return null;
        for (var i = 0; i < acc.Length; i++)
            acc[i] = (float)(acc[i] / total);
        return acc;
    }

    /// <summary>
    /// Weighted k-means producing up to <paramref name="k"/> taste centroids. Deterministic
    /// farthest-point (k-means++ style) init + a few Lloyd iterations, cosine assignment. Returns
    /// only non-empty centroids. With few points it gracefully returns fewer clusters.
    /// </summary>
    public static List<float[]> KMeans(IReadOnlyList<float[]> points, IReadOnlyList<double> weights, int k, int iterations = 8)
    {
        var n = points.Count;
        if (n == 0)
            return [];
        k = Math.Clamp(k, 1, n);
        if (k == 1)
        {
            var c = WeightedCentroid(points.Select((p, i) => ((float[]?)p, weights[i])));
            return c is null ? [] : [c];
        }

        // Farthest-point init: start from the highest-weight point, then repeatedly add the point
        // farthest (in cosine distance) from the already-chosen centroids.
        var seeds = new List<int>();
        var first = 0;
        for (var i = 1; i < n; i++) if (weights[i] > weights[first]) first = i;
        seeds.Add(first);
        while (seeds.Count < k)
        {
            var bestIdx = -1;
            var bestDist = -1.0;
            for (var i = 0; i < n; i++)
            {
                if (seeds.Contains(i))
                    continue;
                var minDist = seeds.Min(s => 1 - Cosine(points[i], points[s]));
                if (minDist > bestDist) { bestDist = minDist; bestIdx = i; }
            }
            if (bestIdx < 0)
                break;
            seeds.Add(bestIdx);
        }

        var centroids = seeds.Select(s => (float[])points[s].Clone()).ToList();
        var assignment = new int[n];

        for (var iter = 0; iter < iterations; iter++)
        {
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                var best = 0;
                var bestSim = double.NegativeInfinity;
                for (var c = 0; c < centroids.Count; c++)
                {
                    var sim = Cosine(points[i], centroids[c]);
                    if (sim > bestSim) { bestSim = sim; best = c; }
                }
                if (assignment[i] != best) { assignment[i] = best; changed = true; }
            }

            for (var c = 0; c < centroids.Count; c++)
            {
                var members = Enumerable.Range(0, n).Where(i => assignment[i] == c).Select(i => ((float[]?)points[i], weights[i]));
                var updated = WeightedCentroid(members);
                if (updated is not null)
                    centroids[c] = updated;
            }

            if (!changed)
                break;
        }

        // Drop centroids that ended up with no members.
        var used = assignment.Distinct().ToHashSet();
        return centroids.Where((_, idx) => used.Contains(idx)).ToList();
    }
}
