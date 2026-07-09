using Recommendations.Abstractions;

namespace Recommendations.Content;

/// <summary>
/// A baseline recommender that surfaces the entities the user already engages with most (highest
/// preference score). It is deliberately trivial — its job is to exercise the engagement → preference
/// pipeline end-to-end so we can verify Part 1 before building real discovery recommenders. It lives in
/// Core only as a bootstrap; real recommenders ship as separate satellite extensions.
/// </summary>
public sealed class SimplePreferenceRecommender : IRecommender
{
    public const string RecommenderId = "cove.community.recommendations.simple";

    private static readonly RecommenderDescriptor Descriptor = new(
        RecommenderId,
        "Simple — more of what you like",
        "Baseline that ranks by your own engagement. Verifies the preference pipeline; not a discovery engine.",
        [RecommendationContext.GlobalFeed, RecommendationContext.SimilarToEntity, RecommendationContext.ScoreItems],
        SourceEntityTypes: ["video", "image", "audio", "text", "performer"],
        TargetEntityTypes: ["video", "image", "audio", "text", "performer"],
        Knobs:
        [
            new RecommenderKnob("recencyWeight", "Recency", 0, 1, 0.3, "How strongly to favor recent engagement (ignored by this baseline)."),
            new RecommenderKnob("noveltySimilarityBalance", "Novelty vs similarity", 0, 1, 0.5, "0 = very similar to history, 1 = more novel (ignored by this baseline)."),
            new RecommenderKnob("whoVsWhat", "Who vs what", 0, 1, 0.5, "0 = content/tags, 1 = performers (ignored by this baseline)."),
        ]);

    public RecommenderDescriptor Describe() => Descriptor;

    public async Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        var method = new PreferenceScoringOptions();
        var top = await request.Core.Preference.GetTopLikedAsync(
            request.UserId, request.TargetEntityType, request.Limit + request.Offset, method, cancellationToken);

        var items = top
            .Skip(request.Offset)
            .Take(request.Limit)
            .Select(s => new ItemScore(s.Entity.EntityType, s.Entity.EntityId, s.Score, s.Confidence, s.Why))
            .ToList();

        return new RecommendationResult(items, Diagnostics: new Dictionary<string, object>
        {
            ["recommender"] = RecommenderId,
            ["pool"] = top.Count,
        });
    }

    public async Task<IReadOnlyList<ItemScore>> ScoreItemsAsync(ScoreRequest request, CancellationToken cancellationToken = default)
    {
        var scores = await request.Core.Preference.ScoreAsync(request.UserId, request.Items, new PreferenceScoringOptions(), cancellationToken);
        return request.Items
            .Select(e =>
            {
                scores.TryGetValue(e, out var s);
                return new ItemScore(e.EntityType, e.EntityId, s?.Score ?? 0, s?.Confidence ?? 0, s?.Why);
            })
            .ToList();
    }
}
