namespace Recommendations.Abstractions;

/// <summary>
/// The context a recommendation request runs in. A recommender declares which it supports.
/// </summary>
public enum RecommendationContext
{
    /// <summary>Personalized "for you" feed, parameterized by target entity type.</summary>
    GlobalFeed,

    /// <summary>"More like this", seeded by a specific entity.</summary>
    SimilarToEntity,

    /// <summary>Score specific item(s) for this user (score + confidence). Powers auto-curation.</summary>
    ScoreItems,

    /// <summary>Re-order an existing candidate set. (Reserved; later phase.)</summary>
    Rerank,

    /// <summary>Emit items to elicit feedback (active learning). (Reserved; later phase.)</summary>
    Training,
}

/// <summary>
/// A reference to any Cove entity by its lowercase type name (e.g. "video", "performer") and id.
/// Kept as string+int so this contract stays free of Cove host types.
/// </summary>
public readonly record struct EntityRef(string EntityType, int EntityId);

/// <summary>Structured reason a score is what it is. Optional, but invaluable for tuning/observability.</summary>
public sealed record Explanation(string Summary, IReadOnlyList<ExplanationFactor> Factors);

/// <summary>One contributing factor in an <see cref="Explanation"/>. Contribution is signed (+/-).</summary>
public sealed record ExplanationFactor(string Key, string Label, double Contribution, string? Detail = null);

/// <summary>A recommender's output for one item: a rank-meaningful score and a confidence in [0,1].</summary>
public sealed record ItemScore(
    string EntityType,
    int EntityId,
    double Score,
    double Confidence,
    Explanation? Why = null);

/// <summary>A standardized, user-facing config knob (e.g. recencyWeight, noveltySimilarityBalance, whoVsWhat).
/// <paramref name="Group"/> lets the UI cluster related knobs (e.g. "Content", "Performers (advanced)"); when a
/// group name ends with "(advanced)" the UI may collapse it by default. <paramref name="Advanced"/> is a hint
/// that the knob is a secondary/expert control.</summary>
public sealed record RecommenderKnob(string Key, string Label, double Min, double Max, double Default, string? Description = null, string? Group = null, bool Advanced = false);

/// <summary>What a recommender can do — surfaced to the UI so it only offers valid context/entity-type combos.</summary>
public sealed record RecommenderDescriptor(
    string Id,
    string Label,
    string? Description,
    IReadOnlyList<RecommendationContext> Contexts,
    IReadOnlyList<string> SourceEntityTypes,
    IReadOnlyList<string> TargetEntityTypes,
    IReadOnlyList<RecommenderKnob>? Knobs = null);

/// <summary>A request for a ranked list of recommendations.</summary>
public sealed record RecommendationRequest(
    RecommendationContext Context,
    int UserId,
    string TargetEntityType,
    EntityRef? Seed,
    int Limit,
    int Offset,
    IReadOnlyDictionary<string, double>? Knobs,
    ICoreServices Core)
{
    /// <summary>When set, restrict a GlobalFeed to a single taste cluster (see <see cref="ITasteClusters"/>).</summary>
    public string? ClusterId { get; init; }

    /// <summary>Soft steer terms (comma-separated tag names/ids) — NOT a hard filter. The recommender biases
    /// results toward these tags AND tags that co-occur with them, so the user can lean the feed toward a
    /// mood without excluding their broader taste.</summary>
    public string? SteerTags { get; init; }

    /// <summary>Soft steer terms (comma-separated performer names/ids) — biases toward these performers and
    /// ones who frequently co-appear with them (a "similar performers" proxy).</summary>
    public string? SteerPerformers { get; init; }

    /// <summary>When set, the recommender scores/ranks EXACTLY these candidate ids (a pre-filtered universe,
    /// e.g. the standard video filter/search) instead of generating its own candidate pool, and does NOT
    /// apply its own "already seen" exclusion. Enables a true filterable, paginated list view.</summary>
    public IReadOnlyList<int>? CandidateIds { get; init; }

    /// <summary>Sort direction for the ranked list. Default (false) = best-first (descending score); true =
    /// worst-first (ascending) — used to inspect what a signal scores LOWEST by isolating it via the knobs.</summary>
    public bool Ascending { get; init; }
}

/// <summary>A ranked recommendation result, plus optional paging cursor and inspector diagnostics.</summary>
/// <param name="TotalCount">Total items in the ranked set (before paging) — drives true pagination in the UI.
/// Null means "unknown / just the page returned".</param>
public sealed record RecommendationResult(
    IReadOnlyList<ItemScore> Items,
    string? Cursor = null,
    IReadOnlyDictionary<string, object>? Diagnostics = null,
    int? TotalCount = null);

/// <summary>A request to score specific items for a user (the ScoreItems capability).</summary>
public sealed record ScoreRequest(
    int UserId,
    IReadOnlyList<EntityRef> Items,
    IReadOnlyDictionary<string, double>? Knobs,
    ICoreServices Core);

/// <summary>
/// The pluggable recommender contract. Satellite extensions register an implementation and publish it
/// via the cross-extension service exchange; Recommendations.Core resolves them live.
/// A recommender composes Core's services (via <see cref="ICoreServices"/>) where it wants, and is free
/// to do its own thing where it differs — Core never forces a taste-modeling approach.
/// </summary>
public interface IRecommender
{
    RecommenderDescriptor Describe();

    Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ItemScore>> ScoreItemsAsync(ScoreRequest request, CancellationToken cancellationToken = default);
}
