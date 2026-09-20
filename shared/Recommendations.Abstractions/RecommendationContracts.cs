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

/// <summary>A named score dimension a recommender produces per item — the overall score, and whatever it breaks
/// that down into (e.g. performers / content / quality / audio). Declaring them lets the UI offer sorting AND
/// range-filtering on each WITHOUT knowing anything about the recommender: "sort at random among videos whose
/// performer score is above X" falls out of one generic control.
/// <paramref name="Min"/>/<paramref name="Max"/> bound the slider. <paramref name="Centered"/> marks a dimension
/// whose neutral point is 0 (a signed "vs your typical" belief) rather than a plain magnitude, so the UI can
/// render it as −/+ around a midpoint.</summary>
public sealed record RecommenderScoreField(
    string Key,
    string Label,
    double Min,
    double Max,
    bool Centered = false,
    string? Description = null);

/// <summary>How a <see cref="ScoreCriterion"/> compares. Deliberately mirrors the numeric comparisons the host's
/// standard filter offers, so a score criterion behaves exactly like any other numeric criterion (duration, frame
/// rate, play count…) rather than being a one-off. Named here rather than reusing the host enum so this contract
/// stays free of Cove host types.</summary>
public enum ScoreComparison
{
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    /// <summary>Inclusive on both bounds, matching the host's numeric BETWEEN.</summary>
    Between,
    NotBetween,
}

/// <summary>A constraint on one <see cref="RecommenderScoreField"/>, in the same
/// (comparison, value, second value) shape the host's numeric criteria use. <paramref name="Value2"/> is the upper
/// bound and is only read for <see cref="ScoreComparison.Between"/> / <see cref="ScoreComparison.NotBetween"/>.
/// An item passes only if every criterion holds.</summary>
public sealed record ScoreCriterion(string Key, ScoreComparison Comparison, double? Value, double? Value2 = null);

/// <summary>What a recommender can do — surfaced to the UI so it only offers valid context/entity-type combos.
/// <paramref name="ScoreFields"/> additionally drives the sort menu and the score range filters.</summary>
public sealed record RecommenderDescriptor(
    string Id,
    string Label,
    string? Description,
    IReadOnlyList<RecommendationContext> Contexts,
    IReadOnlyList<string> SourceEntityTypes,
    IReadOnlyList<string> TargetEntityTypes,
    IReadOnlyList<RecommenderKnob>? Knobs = null,
    IReadOnlyList<RecommenderScoreField>? ScoreFields = null,
    bool SupportsRandomSort = false);

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

    /// <summary>What to rank by. Null or <see cref="SortByOverall"/> = the recommender's overall score. Otherwise a
    /// <see cref="RecommenderScoreField.Key"/> to rank by that single dimension, <see cref="SortRandom"/> to shuffle
    /// (deterministically, via <see cref="RandomSeed"/>), or <see cref="SortByCandidateOrder"/> to keep
    /// <see cref="CandidateIds"/> in the order given — which is how a STANDARD cove sort (date, title, duration…)
    /// composes with score filtering: the host sorts, the recommender only filters.</summary>
    public string? SortKey { get; init; }

    /// <summary>Seed for <see cref="SortRandom"/>, so paging is stable and "reshuffle" is a new seed.</summary>
    public int? RandomSeed { get; init; }

    /// <summary>Constraints on the recommender's declared score fields — non-matching items are dropped BEFORE
    /// paging, so <see cref="RecommendationResult.TotalCount"/> reflects the filtered set.</summary>
    public IReadOnlyList<ScoreCriterion>? ScoreFilters { get; init; }

    public const string SortByOverall = "overall";
    public const string SortRandom = "random";
    public const string SortByCandidateOrder = "candidate";
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
