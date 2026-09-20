using Recommendations.Abstractions;

namespace Recommendations.Toolkit;

/// <summary>One scored candidate, ready to be filtered, sorted and paged.</summary>
/// <param name="Id">The entity id.</param>
/// <param name="Score">The overall score used for ranking and display (0..1).</param>
/// <param name="Confidence">How much data backs the score (0..1).</param>
/// <param name="Dimensions">
/// Per-dimension values, index-aligned with the dimensionKeys passed to <see cref="RankedFeed.Build"/>.
/// May be empty for a recommender that only produces an overall score.
/// </param>
public readonly record struct ScoredCandidate(int Id, double Score, double Confidence, double[] Dimensions);

/// <summary>
/// The shared tail of every recommender: score filtering, sorting, paging and an honest total count.
///
/// This exists because those four things are exactly where recommenders quietly diverge, and the divergence is
/// invisible until a user hits it — one recommender sizes its candidate pool from the page size and so returns
/// nothing on page 2, another omits <see cref="RecommendationResult.TotalCount"/> and silently disables infinite
/// scroll, a third honors sorting by a score dimension while its neighbour ignores it. A recommender should only
/// have to answer "what are the candidates and what do they score"; everything after that is list mechanics that
/// must behave identically no matter which model produced the list.
/// </summary>
public static class RankedFeed
{
    /// <summary>The two dimensions EVERY recommender can produce, whatever its model. Declaring these means the
    /// sort menu and score filters are never empty for a recommender that has no per-aspect breakdown of its own,
    /// so the page behaves the same whichever one is selected.</summary>
    public static readonly IReadOnlyList<string> BasicDimensionKeys = ["overall", "confidence"];

    /// <summary>The <see cref="BasicDimensionKeys"/> as declarable score fields, for a descriptor.</summary>
    public static readonly IReadOnlyList<RecommenderScoreField> BasicScoreFields =
    [
        new("overall", "Overall score", 0, 1, false, "How strongly this recommender rates the item."),
        new("confidence", "Confidence", 0, 1, false, "How much data backs the score — filter this up to see only items the model is sure about."),
    ];

    /// <summary>Dimension values matching <see cref="BasicDimensionKeys"/>.</summary>
    public static double[] BasicDimensions(double score, double confidence) => [score, confidence];

    /// <summary>Filters, sorts and pages <paramref name="candidates"/> into a result.</summary>
    /// <param name="candidates">EVERY candidate the recommender considers — not just the requested page. Paging
    /// happens here, so a pool sized to <see cref="RecommendationRequest.Limit"/> would both break page 2 and
    /// report a total that stops infinite scroll early.</param>
    /// <param name="dimensionKeys">Names for <see cref="ScoredCandidate.Dimensions"/>, in index order. These are
    /// the keys sorting and score filters resolve against, and should match the recommender's declared
    /// <see cref="RecommenderScoreField"/>s.</param>
    /// <param name="explain">Builds the per-item explanation. Called ONLY for items on the returned page, so a
    /// recommender can do expensive name lookups there without paying for the whole library.</param>
    /// <param name="rerank">Optional diversity pass over the sorted list. Applied only when the list is in
    /// best-first overall order, since anything else (a dimension sort, a shuffle, a host-supplied order) is an
    /// explicit instruction that a re-rank would violate.</param>
    public static RecommendationResult Build(
        RecommendationRequest request,
        string entityType,
        IReadOnlyList<ScoredCandidate> candidates,
        IReadOnlyList<string> dimensionKeys,
        Func<ScoredCandidate, Explanation?>? explain = null,
        IReadOnlyDictionary<string, object>? diagnostics = null,
        Func<IReadOnlyList<ScoredCandidate>, IReadOnlyList<ScoredCandidate>>? rerank = null)
    {
        var (page, total) = SelectPage(request, candidates, dimensionKeys, rerank);
        var items = page
            .Select(c => new ItemScore(entityType, c.Id, Math.Clamp(c.Score, 0, 1), Math.Clamp(c.Confidence, 0, 1), explain?.Invoke(c)))
            .ToList();
        return new RecommendationResult(items, TotalCount: total, Diagnostics: diagnostics);
    }

    /// <summary>
    /// The mechanism behind <see cref="Build"/>: filter, sort, optionally re-rank, and cut the requested page,
    /// reporting the total the page came from.
    ///
    /// Exposed separately for recommenders whose per-item explanation needs async work — resolving tag and
    /// performer names, say. Those can page here and then build their own items, which keeps the naming cost
    /// proportional to the page instead of the library while still sharing the list mechanics that must not
    /// differ between recommenders.
    /// </summary>
    public static (IReadOnlyList<ScoredCandidate> Page, int TotalCount) SelectPage(
        RecommendationRequest request,
        IReadOnlyList<ScoredCandidate> candidates,
        IReadOnlyList<string> dimensionKeys,
        Func<IReadOnlyList<ScoredCandidate>, IReadOnlyList<ScoredCandidate>>? rerank = null)
    {
        var kept = ApplyScoreFilters(candidates, dimensionKeys, request.ScoreFilters);
        if (kept.Count == 0) return ([], 0);

        var sortKey = string.IsNullOrWhiteSpace(request.SortKey) ? RecommendationRequest.SortByOverall : request.SortKey!;
        IReadOnlyList<ScoredCandidate> ordered = Sort(kept, request, sortKey, dimensionKeys);

        // Diversity trades score away for spread, so it may only touch a list that is in plain best-first order.
        if (rerank is not null && !request.Ascending && sortKey == RecommendationRequest.SortByOverall)
            ordered = rerank(ordered);

        var page = ordered.Skip(Math.Max(0, request.Offset)).Take(Math.Max(0, request.Limit)).ToList();
        return (page, ordered.Count);
    }

    /// <summary>Index of a dimension key, or -1. Lets a caller read one specific dimension off a candidate.</summary>
    public static int IndexOf(IReadOnlyList<string> dimensionKeys, string? key)
    {
        if (key is null) return -1;
        for (var i = 0; i < dimensionKeys.Count; i++)
            if (string.Equals(dimensionKeys[i], key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Keep only candidates satisfying every score criterion. Comparisons follow the host's standard
    /// numeric-criterion semantics (BETWEEN inclusive, NOT_BETWEEN its exact complement). A criterion naming an
    /// unknown dimension is ignored rather than matching nothing, so a saved filter carried over from another
    /// recommender degrades quietly instead of emptying the page.</summary>
    private static IReadOnlyList<ScoredCandidate> ApplyScoreFilters(
        IReadOnlyList<ScoredCandidate> candidates, IReadOnlyList<string> dimensionKeys, IReadOnlyList<ScoreCriterion>? criteria)
    {
        var resolved = (criteria ?? [])
            .Select(c => (idx: IndexOf(dimensionKeys, c.Key), c.Comparison, c.Value, c.Value2))
            .Where(c => c.idx >= 0 && c.Value is not null)
            .ToList();
        if (resolved.Count == 0) return candidates;

        return candidates.Where(c =>
        {
            foreach (var (idx, comparison, value, value2) in resolved)
            {
                if (idx >= c.Dimensions.Length) continue;
                var v = c.Dimensions[idx];
                var lo = value!.Value;
                var hi = value2 ?? lo;
                var ok = comparison switch
                {
                    ScoreComparison.Equals => v == lo,
                    ScoreComparison.NotEquals => v != lo,
                    ScoreComparison.GreaterThan => v > lo,
                    ScoreComparison.LessThan => v < lo,
                    ScoreComparison.Between => v >= lo && v <= hi,
                    ScoreComparison.NotBetween => v < lo || v > hi,
                    _ => true,
                };
                if (!ok) return false;
            }
            return true;
        }).ToList();
    }

    private static List<ScoredCandidate> Sort(
        IReadOnlyList<ScoredCandidate> candidates, RecommendationRequest request, string sortKey, IReadOnlyList<string> dimensionKeys)
    {
        // Keep the order the host supplied — this is how a standard cove sort (date, title, duration…) composes
        // with score filtering: the host sorts, the recommender only filters.
        if (sortKey == RecommendationRequest.SortByCandidateOrder && request.CandidateIds is { Count: > 0 } given)
        {
            var pos = new Dictionary<int, int>(given.Count);
            for (var i = 0; i < given.Count; i++) pos.TryAdd(given[i], i);
            return candidates.OrderBy(c => pos.GetValueOrDefault(c.Id, int.MaxValue)).ThenBy(c => c.Id).ToList();
        }

        if (sortKey == RecommendationRequest.SortRandom)
        {
            var seed = request.RandomSeed ?? 0;
            return candidates.OrderBy(c => Shuffle(seed, c.Id)).ThenBy(c => c.Id).ToList();
        }

        // A dimension key ranks by that single aspect; anything unrecognized falls back to the overall score
        // rather than erroring, so an unknown sort can never blank the page.
        var di = IndexOf(dimensionKeys, sortKey);
        Func<ScoredCandidate, double> key = di >= 0
            ? c => di < c.Dimensions.Length ? c.Dimensions[di] : 0
            : c => c.Score;
        return (request.Ascending
                ? candidates.OrderBy(key).ThenBy(c => c.Id)
                : candidates.OrderByDescending(key).ThenBy(c => c.Id))
            .ToList();
    }

    /// <summary>A stable pseudo-random ordinal for (seed, id). Deterministic, so the same seed gives the same
    /// shuffle on every page — paging through a randomized feed can neither repeat nor skip items — while a new
    /// seed reshuffles everything.</summary>
    public static double Shuffle(int seed, int id)
    {
        unchecked
        {
            var h = (uint)id * 2654435761u ^ (uint)seed * 2246822519u;
            h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
            return h / (double)uint.MaxValue;
        }
    }
}
