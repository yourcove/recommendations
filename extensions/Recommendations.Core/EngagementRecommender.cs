using Recommendations.Abstractions;
using Recommendations.Toolkit;

namespace Recommendations.Core;

/// <summary>
/// Ranks purely by what you've already engaged with — views, completions, likes, favourites and explicit ratings,
/// fused into one preference score with a confidence (see <see cref="IPreferenceScorer"/>). No clustering, no
/// embeddings, no learned model.
///
/// It answers a different question from the taste models: not "what should I watch next" but "what does this
/// system actually think I like, and why". That makes it the ground truth the other recommenders are judged
/// against, and the reason it lives in Core rather than a satellite — it is built entirely on Core's own scorer,
/// needs no AI extension, and so works on any library from the first rating onward.
/// </summary>
public sealed class EngagementRecommender(IPreferenceScorer scorer) : IRecommender
{
    public const string RecommenderId = "cove.community.recommendations.engagement";
    private readonly IPreferenceScorer _scorer = scorer;

    /// <summary>How many engaged items to rank. Engagement is inherently bounded — you can only have interacted
    /// with so much — so this is a ceiling rather than a page size, and paging happens over the whole set.</summary>
    private const int MaxEngaged = 5000;

    private static readonly RecommenderDescriptor Descriptor = new(
        RecommenderId,
        "Your engagement",
        "Ranks what you've already watched and rated by how much the system thinks you liked it — views, completions, likes, favourites and explicit ratings fused into one score with a confidence. It recommends nothing new; it shows you what your engagement says about your taste, which is the baseline the learning models are worth judging against.",
        [RecommendationContext.GlobalFeed, RecommendationContext.ScoreItems],
        SourceEntityTypes: ["video", "image", "performer", "studio", "tag"],
        TargetEntityTypes: ["video", "image"],
        Knobs: [],
        ScoreFields: RankedFeed.BasicScoreFields,
        SupportsRandomSort: true);

    public RecommenderDescriptor Describe() => Descriptor;

    public async Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        // Ask the scorer for THIS entity type only. Passing the type through (rather than fetching everything and
        // filtering after) is what keeps a "videos" feed from showing images.
        var entityType = string.IsNullOrWhiteSpace(request.TargetEntityType) ? "video" : request.TargetEntityType.ToLowerInvariant();
        var top = await _scorer.GetTopLikedAsync(request.UserId, entityType, MaxEngaged, null, cancellationToken);

        // A filter/search restricts which of them are eligible; the scores themselves are unchanged.
        var allowed = request.CandidateIds is { Count: > 0 } ids ? ids.ToHashSet() : null;

        var explanations = new Dictionary<int, Explanation?>();
        var candidates = new List<ScoredCandidate>(top.Count);
        foreach (var e in top)
        {
            if (!e.Entity.EntityType.Equals(entityType, StringComparison.OrdinalIgnoreCase)) continue;
            if (allowed is not null && !allowed.Contains(e.Entity.EntityId)) continue;
            // The scorer works in [-1,1] (dislike…like); the feed's score is a 0..1 magnitude, so map it across.
            var score = Math.Clamp((e.Score + 1) / 2, 0, 1);
            explanations[e.Entity.EntityId] = e.Why;
            candidates.Add(new ScoredCandidate(e.Entity.EntityId, score, e.Confidence,
                RankedFeed.BasicDimensions(score, e.Confidence)));
        }

        if (candidates.Count == 0)
            return new RecommendationResult([], TotalCount: 0, Diagnostics: new Dictionary<string, object>
            {
                ["recommender"] = RecommenderId,
                ["note"] = $"no engagement recorded for {entityType}s yet — watch or rate a few and they'll appear here",
            });

        return RankedFeed.Build(request, entityType, candidates, RankedFeed.BasicDimensionKeys,
            c => explanations.GetValueOrDefault(c.Id),
            new Dictionary<string, object> { ["recommender"] = RecommenderId, ["engaged"] = candidates.Count });
    }

    public async Task<IReadOnlyList<ItemScore>> ScoreItemsAsync(ScoreRequest request, CancellationToken cancellationToken = default)
    {
        var scores = await _scorer.ScoreAsync(request.UserId, request.Items, null, cancellationToken);
        return request.Items.Select(item =>
        {
            var s = scores.GetValueOrDefault(item, new PreferenceScore(0, 0));
            return new ItemScore(item.EntityType, item.EntityId, Math.Clamp((s.Score + 1) / 2, 0, 1), s.Confidence, s.Why);
        }).ToList();
    }
}
